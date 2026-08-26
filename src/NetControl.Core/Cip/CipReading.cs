namespace NetControl.Core.Cip;

/// <summary>
/// A decoded attribute, or the reason the device would not give it up.
///
/// <para>A refusal is not an exception - the device understood and said no, and which no it was
/// decides what happens next. But a caller that has to check <c>IsSuccess</c> on a raw
/// <see cref="CipResponse"/> and then remember to decode the bytes will eventually forget one of
/// the two. This makes the value unreachable until the status has been looked at.</para>
/// </summary>
/// <typeparam name="T">The decoded attribute.</typeparam>
public readonly struct CipReading<T>
{
    private readonly T? _value;

    private CipReading(T? value, byte status, string? statusText)
    {
        _value = value;
        Status = status;
        StatusText = statusText ?? CipGeneralStatus.Describe(status);
    }

    public byte Status { get; }

    public string StatusText { get; }

    public bool IsSuccess => Status == CipGeneralStatus.Success;

    /// <summary>The decoded attribute. Throws when the device refused, so check first.</summary>
    public T Value => IsSuccess && _value is not null
        ? _value
        : throw new InvalidOperationException($"The device refused this attribute: {StatusText}");

    public static CipReading<T> Ok(T value) => new(value, CipGeneralStatus.Success, null);

    public static CipReading<T> Failed(CipResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return new CipReading<T>(default, response.GeneralStatus, response.StatusText);
    }
}
