namespace NetControl.Core.Plan;

/// <summary>
/// The mask and gateway for every workbook row on one /24 - the two things a project IP workbook
/// does not carry per device, asked for once per subnet at import instead. Both are text, because
/// they go through <see cref="PlanValidation"/> exactly as a CSV cell would and are reported the
/// same way when wrong.
/// </summary>
/// <param name="Mask">e.g. 255.255.255.0.</param>
/// <param name="Gateway">Empty for none.</param>
public sealed record PlanSubnetSettings(string Mask, string Gateway)
{
    /// <summary>What the import dialog starts each subnet at: a /24 and no gateway.</summary>
    public static PlanSubnetSettings Default { get; } = new("255.255.255.0", string.Empty);
}
