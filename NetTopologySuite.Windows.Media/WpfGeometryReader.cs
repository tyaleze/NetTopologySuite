using System;
using System.Collections.Generic;
using System.Windows.Media;
using GeoAPI.Geometries;
using NetTopologySuite.Algorithm;
using NetTopologySuite.Operation.Polygonize;
using NetTopologySuite.Operation.Union;
using NetTopologySuite.Utilities;
using WpfGeometry = System.Windows.Media.Geometry;
using WpfLineSegment = System.Windows.Media.LineSegment;
using WpfPoint = System.Windows.Point;

namespace NetTopologySuite.Windows.Media
{
    ///<summary>
    /// Converts a WPF <see cref="WpfGeometry"/> into a <see cref="IGeometry"/>.
    ///</summary>
    ///<remarks>
    /// <para>
    /// The coordinate system for <see cref="WpfGeometry"/> is typically screen coordinates,
    /// which has the Y axis inverted  relative to the usual JTS coordinate system.
    /// This is rectified during conversion. </para>
    /// <para>
    /// GraphicsPaths to be converted are expected to be linear or flat.
    /// That is, <see cref="System.Windows.Media.Geometry.MayHaveCurves"/> must always return false.
    /// Otherwise an exception will be thrown.
    /// </para>
    /// </remarks>
    /// <author>Martin Davis</author>
    public class WpfGeometryReader
    {
        private static readonly Matrix InvertY = new Matrix(1, 0, 0, -1, 0, 0);

        ///<summary>
        /// Converts a flat path to a <see cref="IGeometry"/>.
        ///</summary>
        /// <param name="pathIt">The path to convert</param>
        /// <param name="geomFact">The GeometryFactory to use</param>
        /// <returns>A Geometry representing the path</returns>
        public static IGeometry Read(WpfGeometry pathIt, IGeometryFactory geomFact)
        {
            var pc = new WpfGeometryReader(geomFact);
            return pc.Read(pathIt);
        }

        ///<summary>
        /// Converts a <see cref="WpfGeometry"/> to a Geometry, flattening it first.
        ///</summary>
        /// <param name="shp">The <see cref="WpfGeometry"/></param>
        /// <param name="tolerance">The tolerance parameter to use</param>
        /// <param name="geomFact">The GeometryFactory to use</param>
        /// <returns>A Geometry representing the shape</returns>
        public static IGeometry Read(WpfGeometry shp, double tolerance, IGeometryFactory geomFact)
        {
            var path = shp.GetFlattenedPathGeometry(tolerance, ToleranceType.Relative);
            return Read(path, geomFact);
        }

        private readonly IGeometryFactory _geometryFactory;

        public WpfGeometryReader(IGeometryFactory geometryFactory)
        {
            _geometryFactory = geometryFactory;
        }

        /// <summary>
        ///  Converts a flat path to a <see cref="IGeometry"/>.
        /// </summary>
        ///  <param name="wpfGeometry">The geometry to convert</param>
        /// <returns>A Geometry representing the path</returns>
        public IGeometry Read(WpfGeometry wpfGeometry)
        {
            var pathGeometry = PathGeometry.CreateFromGeometry(wpfGeometry);
            /* 
             * .Item1 ... Closed
             * .Item2 ... Filled
             * .Item3 ... List<Coordinate[]>
             */
            var pathPtSeq = ToCoordinates(pathGeometry);

            var geoms = new List<IGeometry>();

            var seqIndex = 0;
            while (seqIndex < pathPtSeq.Count)
            {
                // assume next seq is shell
                // TODO: test this
                var pts = pathPtSeq[seqIndex];
                if (pts.Item3.Length == 1)
                    geoms.Add(_geometryFactory.CreatePoint(pts.Item3[0]));
                else if (!pts.Item1) // Closed
                    geoms.Add(_geometryFactory.CreateLineString(pts.Item3));
                else
                {
                    if (!pts.Item2) {
                        geoms.Add(_geometryFactory.CreateLineString(pts.Item3));
                        continue;
                    }

                    var rings = new List<IGeometry>(new[] {_geometryFactory.CreateLinearRing(pts.Item3)});
                    seqIndex++;

                    Coordinate[] holePts;
                    // add holes as long as rings are CCW
                    while (seqIndex < pathPtSeq.Count && IsHole(holePts = pathPtSeq[seqIndex].Item3))
                    {
                        rings.Add(_geometryFactory.CreateLinearRing(holePts));
                        seqIndex++;
                    }

                    var noder = new Noding.Snapround.GeometryNoder(new Geometries.PrecisionModel(100000000.0));
                    var nodedLinework = noder.Node(rings);

                    // Use the polygonizer
                    var p = new Polygonizer(pathGeometry.FillRule == FillRule.EvenOdd);
                    p.Add(new List<IGeometry>(Caster.Upcast<ILineString, IGeometry>(nodedLinework)));
                    var tmpPolygons = p.GetPolygons();
                    if (pathGeometry.FillRule == FillRule.Nonzero)
                    {
                        var unionized = CascadedPolygonUnion.Union(Geometries.GeometryFactory.ToPolygonArray(tmpPolygons));
                        tmpPolygons = new List<IGeometry>(new[] {unionized});
                    }
                    geoms.AddRange(tmpPolygons);
                }
            }

            return _geometryFactory.BuildGeometry(geoms);
        }

        private static bool IsHole(Coordinate[] pts)
        {
            return CGAlgorithms.IsCCW(pts);
        }

        ///<summary>
        /// Extracts the points of the paths in a flat {@link PathIterator} into
        /// a list of Coordinate arrays.
        ///</summary>
        /// <param name="pathGeometry">A path figure collection</param>
        /// <returns>A list of coordinate arrays</returns>
        /// <exception cref="ArgumentException">If a non-linear segment type is encountered</exception>
        private static List<Tuple<bool, bool, Coordinate[]>> ToCoordinates(PathGeometry pathGeometry)
        {
            if (pathGeometry.MayHaveCurves())
                throw new ArgumentException("WPF geometry must not have non-linear segments");

            var coordArrays = new List<Tuple<bool, bool, Coordinate[]>>();

            var pathFigures = pathGeometry.Figures;
            
            foreach (PathFigure pathFigure in pathFigures)
            {
                var coords = NextCoordinateArray(pathFigure);
                coordArrays.Add(Tuple.Create(pathFigure.IsClosed, pathFigure.IsFilled, coords));
            }
            return coordArrays;
        }


        private static Coordinate[] NextCoordinateArray(PathFigure pathFigure)
        {

            var coordinateList = new List<Coordinate>(pathFigure.Segments.Count + 1);

            coordinateList.Add(ToCoordinate(pathFigure.StartPoint));
            foreach (var segment in pathFigure.Segments)
            {
                if (segment is PolyLineSegment)
                {
                    var pseg = segment as PolyLineSegment;
                    foreach (var point in pseg.Points)
                        coordinateList.Add(ToCoordinate(point));
                }
                else if (segment is WpfLineSegment)
                    coordinateList.Add(ToCoordinate(((WpfLineSegment)segment).Point));
                else
                {
                    throw new NotSupportedException(string.Format("'{0}' is not supported", segment.GetType()));
                }
            }
            return coordinateList.ToArray();
        }

        private static Coordinate ToCoordinate(WpfPoint point)
        {
            return new Coordinate(point.X, point.Y);
        }
    }
}