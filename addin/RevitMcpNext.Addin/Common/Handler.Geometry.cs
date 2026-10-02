using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using RevitMcpNext.Addin.Diagnostics;
using RevitMcpNext.Contracts;

namespace RevitMcpNext.Addin.Revit
{
    internal sealed partial class RevitExternalEventHandler
    {
        private static List<XYZ> NormalizeClosedLoop(List<XYZ> points)
        {
            var result = new List<XYZ>(points ?? new List<XYZ>());
            if (result.Count > 1 && result[0].DistanceTo(result[result.Count - 1]) <= 0.000001)
            {
                result.RemoveAt(result.Count - 1);
            }

            return result;
        }

        private static CurveLoop BuildCurveLoop(IReadOnlyList<XYZ> points)
        {
            var loop = new CurveLoop();
            for (int index = 0; index < points.Count; index++)
            {
                loop.Append(Line.CreateBound(points[index], points[(index + 1) % points.Count]));
            }

            return loop;
        }


        private static double PolygonAreaInternal(IReadOnlyList<XYZ> points)
        {
            if (points == null || points.Count < 3) return 0;

            double signedArea = 0;
            for (int index = 0; index < points.Count; index++)
            {
                XYZ current = points[index];
                XYZ next = points[(index + 1) % points.Count];
                signedArea += (current.X * next.Y) - (next.X * current.Y);
            }

            return Math.Abs(signedArea) / 2.0;
        }

        private static double VectorLength(XYZ vector)
        {
            return Math.Sqrt(vector.X * vector.X + vector.Y * vector.Y + vector.Z * vector.Z);
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}
