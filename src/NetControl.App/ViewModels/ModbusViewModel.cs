using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NetControl.Core;
using NetControl.Core.Modbus;
using NetControl.Core.Persistence;
using NetControl.Core.Plan;
using NetControl.Core.Reachability;

namespace NetControl.App.ViewModels;

/// <summary>
/// The Modbus tab: read coils, inputs or registers from one device and show every register decoded
/// the ways it is commonly meant.
///
/// <para><b>Reads only.</b> <see cref="ModbusReader"/> has no write, and <see cref="ModbusFunction"/>
/// has no write function in it. Watching a value change is what this is for - polling at a fixed
/// one second, never faster - and a commissioning tool that could also change a setpoint by a slip
/// of the keyboard would not be one plant IT could hand out.</para>
///
/// <para>Every read is a packet on somebody's network, so it is recorded - once per read, or once
/// at the start and once at the end of a poll, so a minute of polling is two rows rather than sixty.</para>
/// </summary>
public sealed partial class ModbusViewModel : ObservableObject
{
    /// <summary>The poll interval. Fixed, and not configurable downward on purpose.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly Func<ProjectStore> _project;
    private readonly ModbusReader _reader;

    private CancellationTokenSource? _poll;
    private int _pollReads;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReadCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartPollCommand))]
    private string _targetText = string.Empty;

    [ObservableProperty]
    private string? _targetName;

    [ObservableProperty]
    private string _unitIdText = "1";

    [ObservableProperty]
    private ModbusFunctionOption _function = ModbusFunctionOption.All[0];

    [ObservableProperty]
    private string _startText = "0";

    [ObservableProperty]
    private string _countText = "10";

    [ObservableProperty]
    private string _portText = "502";

    [ObservableProperty]
    private ModbusWordOrder _wordOrder = ModbusWordOrder.HighFirst;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReadCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartPollCommand))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReadCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartPollCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopPollCommand))]
    private bool _isPolling;

    [ObservableProperty]
    private string? _status;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string? _errorRemediation;

    /// <summary>The last result, kept so changing the word order re-decodes without reading again.</summary>
    private ModbusReadResult? _last;

    public ModbusViewModel(Func<ProjectStore> project, ModbusReader? reader = null)
    {
        ArgumentNullException.ThrowIfNull(project);

        _project = project;
        _reader = reader ?? new ModbusReader();
    }

    public IReadOnlyList<ModbusFunctionOption> Functions => ModbusFunctionOption.All;

    public IReadOnlyList<ModbusWordOrder> WordOrders { get; } = [ModbusWordOrder.HighFirst, ModbusWordOrder.LowFirst];

    public ObservableCollection<ModbusValueRow> Rows { get; } = [];

    /// <summary>True when the last read was registers, so the 32-bit columns mean something.</summary>
    public bool ShowsRegisters => _last is { Request.IsBits: false };

    /// <summary>Points the tab at a device from the plan or a scan. Sends nothing.</summary>
    public void SetTarget(IPAddress address, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(address);

        StopPoll();
        TargetText = address.ToString();
        TargetName = name;
        ClearError();
    }

    /// <summary>One read.</summary>
    [RelayCommand(CanExecute = nameof(CanRead))]
    private async Task ReadAsync(CancellationToken cancellationToken)
    {
        if (!TryBuild(out ModbusReadRequest? request))
        {
            return;
        }

        ModbusReadResult? result = await ReadOnceAsync(request, cancellationToken).ConfigureAwait(true);

        if (result is not null)
        {
            Record(result.IsSuccess ? EventSeverity.Info : EventSeverity.Warn, request, $"Modbus read: {result.Summary}", result);
        }
    }

    /// <summary>Reads once a second until stopped, or until a read fails.</summary>
    [RelayCommand(CanExecute = nameof(CanRead))]
    private async Task StartPollAsync()
    {
        if (!TryBuild(out ModbusReadRequest? request))
        {
            return;
        }

        _poll = new CancellationTokenSource();
        _pollReads = 0;
        IsPolling = true;
        CancellationToken token = _poll.Token;

        Record(EventSeverity.Info, request, $"Modbus poll started: {request.Describe()}, once a second.", null);

        ModbusReadResult? last = null;

        try
        {
            while (!token.IsCancellationRequested)
            {
                last = await ReadOnceAsync(request, token).ConfigureAwait(true);

                if (last is null || !last.IsSuccess)
                {
                    break;
                }

                _pollReads++;
                await Task.Delay(PollInterval, token).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped. The closing row below says how many reads it was.
        }
        finally
        {
            IsPolling = false;
            _poll?.Dispose();
            _poll = null;

            Record(
                last is { IsSuccess: false } ? EventSeverity.Warn : EventSeverity.Info,
                request,
                last is { IsSuccess: false }
                    ? $"Modbus poll stopped after {_pollReads} read(s): {last.Summary}"
                    : $"Modbus poll stopped after {_pollReads} read(s).",
                null);
        }
    }

    [RelayCommand(CanExecute = nameof(CanStopPoll))]
    private void StopPoll() => _poll?.Cancel();

    private bool CanStopPoll() => IsPolling;

    partial void OnWordOrderChanged(ModbusWordOrder value) => ShowRows();

    partial void OnTargetTextChanged(string value)
    {
        TargetName = null;
    }

    private bool CanRead() => !IsBusy && !IsPolling && !string.IsNullOrWhiteSpace(TargetText);

    /// <summary>
    /// One read with the busy flag and the error banner handled. Returns null when nothing came
    /// back to judge - the banner then says why.
    /// </summary>
    private async Task<ModbusReadResult?> ReadOnceAsync(ModbusReadRequest request, CancellationToken cancellationToken)
    {
        ClearError();
        IsBusy = true;

        try
        {
            ModbusReadResult result = await _reader.ReadAsync(request, cancellationToken).ConfigureAwait(true);
            _last = result;
            Status = result.Summary;

            if (result.IsSuccess)
            {
                ShowRows();
            }
            else
            {
                SetError(result.Summary, null);
            }

            return result;
        }
        catch (NetControlException failure) when (failure is ModbusException or ReachabilityException)
        {
            SetError(failure.Message, failure.Remediation);
            Status = failure.Message;
            Record(EventSeverity.Warn, request, $"Modbus read failed: {failure.Message}", null);
            return null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ShowRows()
    {
        Rows.Clear();

        if (_last is { IsSuccess: true } last)
        {
            foreach (ModbusValueRow row in ModbusValueRow.From(last, WordOrder))
            {
                Rows.Add(row);
            }
        }

        OnPropertyChanged(nameof(ShowsRegisters));
    }

    /// <summary>Parses the five boxes, saying what is wrong with the first one that does not parse.</summary>
    private bool TryBuild([NotNullWhen(true)] out ModbusReadRequest? request)
    {
        request = null;
        ClearError();

        if (!PlanValidation.TryParseIPv4(TargetText.Trim(), out IPAddress? address))
        {
            SetError($"'{TargetText.Trim()}' is not an IPv4 address.", "Type four numbers, or right-click a plan row > Read Modbus registers.");
            return false;
        }

        if (!byte.TryParse(UnitIdText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out byte unit))
        {
            SetError($"Unit id '{UnitIdText.Trim()}' is not 0-255.", "1 for most devices on Ethernet; behind a gateway, the serial address.");
            return false;
        }

        if (!ushort.TryParse(StartText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out ushort start))
        {
            SetError($"Start '{StartText.Trim()}' is not an address from 0 to 65535.", "Zero-based: holding register 40001 is start 0.");
            return false;
        }

        if (!ushort.TryParse(CountText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out ushort count))
        {
            SetError($"Count '{CountText.Trim()}' is not a number.", null);
            return false;
        }

        if (!int.TryParse(PortText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int port))
        {
            SetError($"Port '{PortText.Trim()}' is not a number.", "502 unless the device's manual says otherwise.");
            return false;
        }

        var built = new ModbusReadRequest
        {
            Address = address,
            Port = port,
            UnitId = unit,
            Function = Function.Value,
            Start = start,
            Count = count,
        };

        if (built.Problem() is { } problem)
        {
            SetError(problem, null);
            return false;
        }

        if (!UnicastTarget.TryCheck(address, null, out string? notOneHost))
        {
            SetError(notOneHost ?? $"{address} is not one device.", "Modbus reads go to one device at a time.");
            return false;
        }

        request = built;
        return true;
    }

    private void Record(EventSeverity severity, ModbusReadRequest request, string message, ModbusReadResult? result)
    {
        try
        {
            _project().Events.Append(
                severity,
                EventCategory.Modbus,
                message,
                target: request.Address.ToString(),
                detail: new EventDetail()
                    .Add("function", request.Function)
                    .Add("unit", request.UnitId)
                    .Add("start", request.Start)
                    .Add("count", request.Count)
                    .Add("port", request.Port)
                    .Add("exception", result?.ExceptionCode));
        }
        catch (PersistenceException ex)
        {
            SetError($"Could not record that in the project: {ex.Message}", ex.Remediation);
        }
    }

    private void SetError(string message, string? remediation)
    {
        ErrorMessage = message;
        ErrorRemediation = remediation;
    }

    private void ClearError()
    {
        ErrorMessage = null;
        ErrorRemediation = null;
    }
}
