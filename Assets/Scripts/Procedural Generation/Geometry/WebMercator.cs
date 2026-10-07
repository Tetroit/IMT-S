using System;
using UnityEngine;

namespace ProceduralGeneration
{
    /// <summary>
    /// Web Mercator (EPSG:3857) helpers shared by the tile grid and the geometry config.
    /// </summary>
    public static class WebMercator
    {
        public const double EarthRadius = 6378137.0;
        /// <summary>Half the world width, mercator x and y lie in [-OriginShift, OriginShift].</summary>
        public const double OriginShift = Math.PI * EarthRadius;
        public const double WorldSize = 2 * OriginShift;
        /// <summary>Zoom levels served by the ArcGIS World Imagery tile service.</summary>
        public const int MaxZoom = 23;

        public static int TileCount(int zoom) => 1 << zoom;

        /// <summary>Edge length of a tile at <paramref name="zoom"/> in mercator units.</summary>
        public static double TileSize(int zoom) => WorldSize / TileCount(zoom);

        /// <summary>Latitude in radians of a mercator y.</summary>
        public static double LatitudeFromY(double y) => 2 * Math.Atan(Math.Exp(y / EarthRadius)) - Math.PI / 2;

        /// <summary>
        /// Mercator units per ground metre at mercator y. Mercator stretches distances by 1 / cos(latitude).
        /// </summary>
        public static double MercatorPerMeter(double y) => 1.0 / Math.Cos(LatitudeFromY(y));
    }

    /// <summary>
    /// A tile of the Web Mercator XYZ grid (the scheme ArcGIS, OSM and Google use).
    /// x grows east, y grows south, tile (0, 0) is the north-west corner of the world.
    /// </summary>
    [Serializable]
    public struct TileBounds : IEquatable<TileBounds>, IComparable<TileBounds>
    {
        public int x;
        public int y;
        public int zoom;

        public TileBounds(int x, int y, int zoom)
        {
            this.x = x;
            this.y = y;
            this.zoom = zoom;
        }

        public double size => WebMercator.TileSize(zoom);
        public double minX => -WebMercator.OriginShift + x * size;
        public double maxX => minX + size;
        public double maxY => WebMercator.OriginShift - y * size;
        public double minY => maxY - size;

        /// <summary>Mercator south-west corner.</summary>
        public DoubleVector2 min => new DoubleVector2(minX, minY);
        /// <summary>Mercator north-east corner.</summary>
        public DoubleVector2 max => new DoubleVector2(maxX, maxY);
        public DoubleVector2 center => new DoubleVector2(minX + size / 2, maxY - size / 2);

        public TileBounds parent => new TileBounds(x >> 1, y >> 1, zoom - 1);

        /// <summary>Child 0..3, row major from the north-west.</summary>
        public TileBounds GetChild(int index) => new TileBounds(x * 2 + (index & 1), y * 2 + (index >> 1), zoom + 1);

        public bool isValid => zoom >= 0 && x >= 0 && y >= 0 && x < WebMercator.TileCount(zoom) && y < WebMercator.TileCount(zoom);

        /// <summary>Tile at <paramref name="zoom"/> containing a mercator point, clamped to the world.</summary>
        public static TileBounds FromMercator(DoubleVector2 mercator, int zoom)
        {
            double size = WebMercator.TileSize(zoom);
            int count = WebMercator.TileCount(zoom);
            int x = (int)Math.Floor((mercator.x + WebMercator.OriginShift) / size);
            int y = (int)Math.Floor((WebMercator.OriginShift - mercator.y) / size);
            return new TileBounds(Mathf.Clamp(x, 0, count - 1), Mathf.Clamp(y, 0, count - 1), zoom);
        }

        public string GetArcGisUrl(string service = "World_Imagery") =>
            $"https://server.arcgisonline.com/ArcGIS/rest/services/{service}/MapServer/tile/{zoom}/{y}/{x}";

        public bool Equals(TileBounds other) => x == other.x && y == other.y && zoom == other.zoom;
        public override bool Equals(object obj) => obj is TileBounds other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(x, y, zoom);

        /// <summary>Coarse tiles first, then north to south, west to east.</summary>
        public int CompareTo(TileBounds other)
        {
            if (zoom != other.zoom) return zoom.CompareTo(other.zoom);
            if (y != other.y) return y.CompareTo(other.y);
            return x.CompareTo(other.x);
        }

        public override string ToString() => $"z{zoom} ({x}, {y})";
    }
}
