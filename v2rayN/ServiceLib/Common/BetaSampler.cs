namespace ServiceLib.Common;

/// <summary>
/// Draws from a Beta distribution, which is what lets Auto Mode balance "use the source
/// that has been working" against "give the untested source a chance". A source with
/// little evidence has a wide posterior and so occasionally wins a draw; one with a long
/// record of failure narrows toward zero and stops being picked.
/// </summary>
public static class BetaSampler
{
    /// <summary>Beta(alpha, beta) via two Gamma draws — no distribution library needed.</summary>
    public static double Sample(double alpha, double beta)
    {
        var x = SampleGamma(Math.Max(0.05, alpha));
        var y = SampleGamma(Math.Max(0.05, beta));
        return x + y <= 0 ? 0 : x / (x + y);
    }

    /// <summary>Marsaglia-Tsang gamma sampler, with the standard boost for shape &lt; 1.</summary>
    public static double SampleGamma(double shape)
    {
        if (shape < 1)
        {
            return SampleGamma(shape + 1) * Math.Pow(NextDouble(), 1.0 / shape);
        }

        var d = shape - (1.0 / 3.0);
        var c = 1.0 / Math.Sqrt(9 * d);
        while (true)
        {
            double x, v;
            do
            {
                x = NextGaussian();
                v = 1 + (c * x);
            } while (v <= 0);

            v = v * v * v;
            var u = NextDouble();
            if (u < 1 - (0.0331 * x * x * x * x))
            {
                return d * v;
            }
            if (Math.Log(u) < (0.5 * x * x) + (d * (1 - v + Math.Log(v))))
            {
                return d * v;
            }
        }
    }

    /// <summary>Never exactly zero, which would blow up the logarithms above.</summary>
    private static double NextDouble()
    {
        return Math.Max(1e-12, Random.Shared.NextDouble());
    }

    private static double NextGaussian()
    {
        var u1 = NextDouble();
        var u2 = NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}
