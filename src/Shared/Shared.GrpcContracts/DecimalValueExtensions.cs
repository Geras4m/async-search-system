namespace Shared.GrpcContracts;

/// <summary>
/// Conversions between <see cref="decimal"/> and the protobuf <see cref="DecimalValue"/> message.
/// </summary>
/// <remarks>
/// Protobuf has no decimal scalar type. Transporting money as <c>double</c> loses precision,
/// so prices travel as a (units, nanos) pair and are rebuilt exactly on the other side.
/// </remarks>
public static class DecimalValueExtensions
{
    /// <summary>Number of nanos in one whole unit.</summary>
    private const decimal NanoFactor = 1_000_000_000m;

    /// <summary>
    /// Converts a <see cref="decimal"/> into its protobuf representation.
    /// </summary>
    /// <param name="value">The amount to convert.</param>
    /// <returns>A <see cref="DecimalValue"/> carrying the same amount without loss of precision.</returns>
    /// <exception cref="OverflowException">
    /// Thrown when the whole part of <paramref name="value"/> does not fit in an <see cref="long"/>.
    /// </exception>
    public static DecimalValue ToDecimalValue(this decimal value)
    {
        var units = decimal.ToInt64(decimal.Truncate(value));
        var nanos = decimal.ToInt32((value - units) * NanoFactor);

        return new DecimalValue { Units = units, Nanos = nanos };
    }

    /// <summary>
    /// Converts a protobuf <see cref="DecimalValue"/> back into a <see cref="decimal"/>.
    /// </summary>
    /// <param name="value">The protobuf amount. May be <see langword="null"/> on the wire.</param>
    /// <returns>The reconstructed amount, or <c>0</c> when <paramref name="value"/> is <see langword="null"/>.</returns>
    public static decimal ToDecimal(this DecimalValue? value) =>
        value is null ? 0m : value.Units + (value.Nanos / NanoFactor);
}
