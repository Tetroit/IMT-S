using UnityEngine;

namespace IMT.Thermal
{
    /// <summary>
    /// How a surface meets the air (item 6, wind; decided 2026-10-19). Every surface shares one convection formula,
    /// h = √((0.84·|ΔT|^(1/3))² + (a·U10^b)²), and its group sets only a and b. Two kinds of group: objects standing
    /// in the wind, and the land surface, which is the ground and the vegetation on it, set by roughness and height.
    /// A road is land, not an object: to the air it is an extended flat surface, like a field.
    /// </summary>
    public enum ThermalSurfaceGroup
    {
        [InspectorName("Object (houses, vehicles, walls, props)")] Object = 0,
        [InspectorName("Ground/Paving")] Paving = 10,
        [InspectorName("Ground/Bare soil")] BareSoil = 11,
        [InspectorName("Ground/Grass")] Grass = 12,
        [InspectorName("Ground/Crops")] Crops = 13,
        [InspectorName("Vegetation/Hedge or shrub (~5 m)")] Hedge = 20,
        [InspectorName("Vegetation/Tree (~10 m)")] Tree = 21,
        [InspectorName("Vegetation/Forest (~20 m)")] Forest = 22,
    }

    public static class ThermalSurfaceGroups
    {
        /// <summary>
        /// The group's wind numbers: a in W m⁻² K⁻¹ per (m/s)^b, with U10 the 10 m open-terrain wind in m/s, the
        /// wind KNMI and ERA5-Land report. Sources and the comparison behind them: decision log, item 6.
        /// </summary>
        public static (float a, float b) Wind(ThermalSurfaceGroup group)
        {
            switch (group)
            {
                // Land, b = 1: a = ρc_p·k²/(ln(10/z0m)·ln(2/z0h)), wind at 10 m and air at 2 m, z0h = z0m/10 (FAO-56
                // form), ρc_p = 1200 J m⁻³ K⁻¹, k = 0.41. Paving and bare soil share z0m = 0.005 m for now; item 7
                // (latent heat) will tell them apart, since only soil evaporates.
                case ThermalSurfaceGroup.Paving: return (3.200f, 1f);
                case ThermalSurfaceGroup.BareSoil: return (3.200f, 1f);
                case ThermalSurfaceGroup.Grass: return (5.340f, 1f);      // z0m 0.03 m
                case ThermalSurfaceGroup.Crops: return (8.267f, 1f);      // z0m 0.10 m

                // Vegetation, the crown as one surface, b = 1: the canopy's own bulk transfer, d = ⅔h and z0m = 0.1h
                // with z0h = z0m (closed canopies). U10 is taken up to a 40 m blending height over open terrain
                // (z0 0.03 m) and brought down over the canopy: a = ρc_p·k²·[ln(40/0.03)/ln(10/0.03)]/ln((40−d)/z0m)².
                case ThermalSurfaceGroup.Hedge: return (13.545f, 1f);     // h 5 m
                case ThermalSurfaceGroup.Tree: return (20.320f, 1f);      // h 10 m
                case ThermalSurfaceGroup.Forest: return (37.240f, 1f);    // h 20 m

                // Objects: MoWiTT's windward fit against the 10 m wind, convection only (Yazdanian & Klems, 1994).
                default: return (2.38f, 0.89f);
            }
        }
    }
}
