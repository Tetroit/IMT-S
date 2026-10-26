using System;

namespace IMT.Thermal
{
    /// <summary>
    /// Special functions the physics needs and .NET lacks. Numerics, not physics: each is checked against SciPy by the
    /// check of the physics that uses it.
    /// </summary>
    public static class ThermalSpecialFunctions
    {
        static readonly double k_TwoOverSqrtPi = 2.0 / Math.Sqrt(Math.PI);
        static readonly double k_OneOverSqrtPi = 1.0 / Math.Sqrt(Math.PI);

        /// <summary>
        /// The scaled complementary error function, erfcx(x) = exp(x²)·erfc(x), for x ≥ 0: 1 at 0, falling as
        /// 1/(x√π) for large x. Item 5's stored heat steps by R(t) = 1 − erfcx(β√t) (Thermal → History Check).
        /// Computed as itself, not as exp(x²) times erfc(x), which overflow and underflow long before x = 30.
        /// Below 2 from erf's Taylor series, from 2 up by Laplace's continued fraction (modified Lentz). Within 2e-13
        /// relative of SciPy from 0 to 1e4, at most 74 terms (at 2).
        /// </summary>
        public static double Erfcx(double x)
        {
            if (!(x >= 0.0))
                throw new ArgumentOutOfRangeException(nameof(x), x, "erfcx is only written for x >= 0");

            if (x < 2.0)
            {
                // erf(x) = 2/√π · Σ (−1)ⁿ x^(2n+1) / (n! (2n+1))
                double x2 = x * x, term = x, sum = x;
                for (int n = 1; n < 80; n++)
                {
                    term *= -x2 / n;
                    double add = term / (2 * n + 1);
                    sum += add;
                    if (Math.Abs(add) < 1e-17 * Math.Abs(sum))
                        break;
                }
                return Math.Exp(x2) * (1.0 - k_TwoOverSqrtPi * sum);
            }

            // erfc(x) = exp(−x²)/√π · 1/(x + ½/(x + 1/(x + (3/2)/(x + 2/(x + …))))): numerators 1, ½, 1, 3/2, …
            const double tiny = 1e-300;
            double f = tiny, c = tiny, d = 0.0;
            for (int j = 1; j < 500; j++)
            {
                double a = j == 1 ? 1.0 : (j - 1) * 0.5;
                d = 1.0 / (x + a * d);
                c = x + a / c;
                double delta = c * d;
                f *= delta;
                if (Math.Abs(delta - 1.0) < 1e-16)
                    break;
            }
            return f * k_OneOverSqrtPi;
        }
    }
}
