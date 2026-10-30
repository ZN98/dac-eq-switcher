using System;

namespace DacEqSwitcher
{
    /// In-place radix-2 complex FFT. Unnormalised in both directions, like FFTW:
    /// forward uses e^(-i…), backward e^(+i…).
    static class Fft
    {
        public static void Transform(double[] re, double[] im, bool backward)
        {
            int n = re.Length;
            if (n == 0 || (n & (n - 1)) != 0) throw new ArgumentException("FFT length must be a power of two");
            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i < j)
                {
                    double t = re[i]; re[i] = re[j]; re[j] = t;
                    t = im[i]; im[i] = im[j]; im[j] = t;
                }
            }
            double sign = backward ? 1 : -1;
            for (int len = 2; len <= n; len <<= 1)
            {
                double ang = sign * 2 * Math.PI / len;
                double wr = Math.Cos(ang), wi = Math.Sin(ang);
                int half = len >> 1;
                for (int i = 0; i < n; i += len)
                {
                    double cr = 1, ci = 0;
                    for (int k = 0; k < half; k++)
                    {
                        int a = i + k, b = a + half;
                        double xr = re[b] * cr - im[b] * ci, xi = re[b] * ci + im[b] * cr;
                        re[b] = re[a] - xr; im[b] = im[a] - xi;
                        re[a] += xr; im[a] += xi;
                        double t = cr * wr - ci * wi;
                        ci = cr * wi + ci * wr;
                        cr = t;
                    }
                }
            }
        }

        public static int NextPow2(long n)
        {
            int p = 1;
            while (p < n) p <<= 1;
            return p;
        }
    }
}
