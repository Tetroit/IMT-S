using System;
using UnityEngine;

namespace IMT.Thermal
{
    /// <summary>
    /// A site's clear-sky climate through the year, for <see cref="ThermalEnvironment"/> (D-016): where the
    /// site is, and per month the clear day's air - minimum, maximum and Parton-Logan a, b, c - its dew point
    /// and the Linke turbidity; since item 6, optionally its 10 m wind at each whole UTC hour. Built outside Unity by IMTNS/site_profile.py and imported from its
    /// .siteprofile file by ThermalSiteProfileImporter, not edited by hand.
    ///
    /// Between months the values blend linearly from one mid-month anchor (the 15th, 00:00 UTC) to the next.
    /// That is a pure, continuous function of time: nothing steps at a month boundary, and D-008's history
    /// can sample any past instant. The wind also blends between whole hours, across midnight too.
    /// </summary>
    public class ThermalSiteProfile : ScriptableObject
    {
        /// <summary>One month's clear day, as the profile gives it on the 15th.</summary>
        [Serializable]
        public struct Month
        {
            public double minK, maxK;      // the clear day's lowest and highest air temperature
            public double dewPointK;       // held through the day
            public double a, b, c;         // Parton-Logan, hours: the maximum's lag, the night decay, the minimum after sunrise
            public double linkeTurbidity;
        }

        [SerializeField] string m_DisplayName;
        [SerializeField] double m_Latitude;
        [SerializeField] double m_Longitude;
        [SerializeField] float m_Altitude;
        [SerializeField, TextArea(2, 8)] string m_Provenance;
        [SerializeField] Month[] m_Months;
        // Item 6: the clear day's 10 m wind, m/s, at 00, 01 ... 23 UTC on each month's 15th, January's 24 first.
        // Empty for a profile built without wind; ThermalEnvironment's own setting drives it then.
        [SerializeField] double[] m_WindHourly;

        public string DisplayName => string.IsNullOrEmpty(m_DisplayName) ? name : m_DisplayName;
        public double Latitude => m_Latitude;
        /// <summary>Degrees, east positive.</summary>
        public double Longitude => m_Longitude;
        /// <summary>Height above sea level, m.</summary>
        public float Altitude => m_Altitude;
        /// <summary>Where the numbers came from: dataset, period, method.</summary>
        public string Provenance => m_Provenance;
        public bool IsValid => m_Months != null && m_Months.Length == 12;
        /// <summary>Whether the profile carries the wind (item 6).</summary>
        public bool HasWind => m_WindHourly != null && m_WindHourly.Length == 12 * 24;

        /// <summary>A month's own wind at a whole UTC hour, m/s: what <see cref="WindSpeed"/> gives then on the 15th.</summary>
        public double GetWind(int month, int hour) => m_WindHourly[(month - 1) * 24 + hour];

        /// <summary>
        /// The clear day's 10 m wind at <paramref name="utc"/>, m/s: in each of the two neighbouring months, the two
        /// whole hours around the instant blended, then the months blended as <see cref="Sample"/> blends them.
        /// </summary>
        public double WindSpeed(DateTime utc)
        {
            var anchor = new DateTime(utc.Year, utc.Month, 15, 0, 0, 0, DateTimeKind.Utc);
            DateTime from = utc >= anchor ? anchor : anchor.AddMonths(-1);
            DateTime to = from.AddMonths(1);
            double w = (utc - from).TotalSeconds / (to - from).TotalSeconds;
            double hours = utc.TimeOfDay.TotalHours;
            int hour = (int)Math.Floor(hours);
            double f = hours - hour;
            double At(int month)
            {
                int row = (month - 1) * 24;
                double here = m_WindHourly[row + hour], next = m_WindHourly[row + (hour + 1) % 24];
                return here + (next - here) * f;
            }
            double p = At(from.Month), n = At(to.Month);
            return p + (n - p) * w;
        }

        /// <summary>A month's own values, 1 = January: what <see cref="Sample"/> gives on the 15th.</summary>
        public Month GetMonth(int month) => m_Months[month - 1];

        /// <summary>
        /// The site's clear day at <paramref name="utc"/>: the two neighbouring mid-month values, blended by
        /// how far the instant lies between them.
        /// </summary>
        public Month Sample(DateTime utc)
        {
            var anchor = new DateTime(utc.Year, utc.Month, 15, 0, 0, 0, DateTimeKind.Utc);
            DateTime from = utc >= anchor ? anchor : anchor.AddMonths(-1);
            DateTime to = from.AddMonths(1);
            double w = (utc - from).TotalSeconds / (to - from).TotalSeconds;
            Month p = m_Months[from.Month - 1], n = m_Months[to.Month - 1];
            double Blend(double x, double y) => x + (y - x) * w;
            return new Month
            {
                minK = Blend(p.minK, n.minK),
                maxK = Blend(p.maxK, n.maxK),
                dewPointK = Blend(p.dewPointK, n.dewPointK),
                a = Blend(p.a, n.a),
                b = Blend(p.b, n.b),
                c = Blend(p.c, n.c),
                linkeTurbidity = Blend(p.linkeTurbidity, n.linkeTurbidity),
            };
        }

        /// <summary>The whole profile at once - for the importer.</summary>
        public void Set(string displayName, double latitude, double longitude, float altitude, string provenance,
                        Month[] months, double[] windHourly = null)
        {
            if (months == null || months.Length != 12)
                throw new ArgumentException("a site profile has twelve months, January first");
            if (windHourly != null && windHourly.Length != 12 * 24)
                throw new ArgumentException("a profile's wind has 24 hours for each of twelve months");
            m_DisplayName = displayName;
            m_Latitude = latitude;
            m_Longitude = longitude;
            m_Altitude = altitude;
            m_Provenance = provenance;
            m_Months = (Month[])months.Clone();
            m_WindHourly = windHourly != null ? (double[])windHourly.Clone() : new double[0];
        }
    }
}
