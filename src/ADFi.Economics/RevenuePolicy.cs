namespace ADFi.Economics;

public sealed record NetRevenueInputs(
    decimal CollectedSubscriptionReceiptsUsd,
    decimal PaymentProcessingFeesUsd,
    decimal IndirectTransactionTaxesUsd,
    decimal RefundsUsd,
    decimal ReversalsAndChargebacksUsd);

public sealed record RevenueAllocation(
    decimal NetSubscriptionRevenueUsd,
    decimal CommunityEconomyUsd,
    decimal AdfiUsd,
    string PolicyVersion);

public static class RevenuePolicy
{
    public const decimal CommunityShare = 0.70m;
    public const decimal AdfiShare = 0.30m;
    public const string CurrentVersion = "2026-10-02.1";

    public static RevenueAllocation Calculate(NetRevenueInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        EnsureNonNegative(inputs.CollectedSubscriptionReceiptsUsd, nameof(inputs.CollectedSubscriptionReceiptsUsd));
        EnsureNonNegative(inputs.PaymentProcessingFeesUsd, nameof(inputs.PaymentProcessingFeesUsd));
        EnsureNonNegative(inputs.IndirectTransactionTaxesUsd, nameof(inputs.IndirectTransactionTaxesUsd));
        EnsureNonNegative(inputs.RefundsUsd, nameof(inputs.RefundsUsd));
        EnsureNonNegative(inputs.ReversalsAndChargebacksUsd, nameof(inputs.ReversalsAndChargebacksUsd));

        var deductions = inputs.PaymentProcessingFeesUsd
            + inputs.IndirectTransactionTaxesUsd
            + inputs.RefundsUsd
            + inputs.ReversalsAndChargebacksUsd;
        var net = decimal.Round(Math.Max(0m, inputs.CollectedSubscriptionReceiptsUsd - deductions), 2);
        var community = decimal.Round(net * CommunityShare, 2, MidpointRounding.ToEven);
        var adfi = net - community;

        return new(net, community, adfi, CurrentVersion);
    }

    private static void EnsureNonNegative(decimal value, string parameterName)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Economic inputs cannot be negative.");
        }
    }
}

