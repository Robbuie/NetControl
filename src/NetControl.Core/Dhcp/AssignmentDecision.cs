namespace NetControl.Core.Dhcp;

/// <summary>
/// What the policy decided about one request, and why. The reason is not decoration — it is what
/// the request log shows next to an unmatched MAC, and it is the difference between "nothing
/// happened" and "00:1D:9C:.. is not in the plan".
/// </summary>
public sealed record AssignmentDecision
{
    private AssignmentDecision(DeviceAssignment? assignment, string reason)
    {
        Assignment = assignment;
        Reason = reason;
    }

    public DeviceAssignment? Assignment { get; }

    public string Reason { get; }

    public bool ShouldServe => Assignment is not null;

    public static AssignmentDecision Serve(DeviceAssignment assignment, string reason)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        return new AssignmentDecision(assignment, reason);
    }

    public static AssignmentDecision Ignore(string reason) => new(null, reason);
}
