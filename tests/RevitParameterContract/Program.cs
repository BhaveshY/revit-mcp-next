using System;
using System.Linq;
using System.Reflection;
using Autodesk.Revit.DB;
using RevitMcpNext.Addin.Revit;

namespace RevitMcpNext.RevitParameterContract
{
    internal static class Program
    {
        private static int Main()
        {
            try
            {
                AssertForgeTypeId(UnitTypeId.Millimeters, ParameterWriteContract.ResolveUnitTypeId("mm"), "mm unit mapping");
                AssertForgeTypeId(UnitTypeId.Meters, ParameterWriteContract.ResolveUnitTypeId("meters"), "meters unit mapping");
                AssertForgeTypeId(UnitTypeId.Feet, ParameterWriteContract.ResolveUnitTypeId("ft"), "feet unit mapping");
                AssertForgeTypeId(UnitTypeId.Inches, ParameterWriteContract.ResolveUnitTypeId("inches"), "inches unit mapping");

                Assert(UnitUtils.IsValidUnit(SpecTypeId.Length, UnitTypeId.Millimeters), "Revit 2024 rejected millimeters for Length.");
                Assert(!UnitUtils.IsValidUnit(SpecTypeId.Area, UnitTypeId.Millimeters), "Revit 2024 accepted a length unit for Area.");
                double internalWidth = UnitUtils.ConvertToInternalUnits(2510.0, UnitTypeId.Millimeters);
                Assert(Math.Abs(internalWidth - 8.2349081365) < 0.0000001, "2510 mm conversion did not match Revit internal feet.");

                Assert(ParameterWriteContract.IsYesNoDataType(SpecTypeId.Boolean.YesNo), "Yes/No spec detection failed.");
                Assert(!ParameterWriteContract.IsYesNoDataType(SpecTypeId.Length), "Length was incorrectly classified as Yes/No.");
                Assert(ParameterWriteContract.IsInternalUnitToken("revit-internal"), "Revit internal unit token was not recognized.");

                Type parameterType = typeof(Parameter);
                RequireMethod(typeof(Definition), "GetDataType", Type.EmptyTypes);
                RequireMethod(parameterType, "GetUnitTypeId", Type.EmptyTypes);
                RequireMethod(typeof(UnitUtils), "IsMeasurableSpec", new[] { typeof(ForgeTypeId) });
                RequireMethod(typeof(UnitUtils), "IsValidUnit", new[] { typeof(ForgeTypeId), typeof(ForgeTypeId) });
                RequireMethod(typeof(WorksharingUtils), "GetCheckoutStatus", new[] { typeof(Document), typeof(ElementId), typeof(string).MakeByRefType() });
                Assert(Enum.GetNames(typeof(CheckoutStatus)).Contains("OwnedByOtherUser"), "OwnedByOtherUser checkout status is unavailable.");

                bool unsupportedUnitBlocked = false;
                try
                {
                    ParameterWriteContract.ResolveUnitTypeId("parsecs");
                }
                catch (InvalidOperationException)
                {
                    unsupportedUnitBlocked = true;
                }
                Assert(unsupportedUnitBlocked, "Unsupported unit tokens were not blocked.");

                Console.WriteLine("Revit 2024 parameter contract checks passed.");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                return 1;
            }
        }

        private static MethodInfo RequireMethod(Type type, string name, Type[] arguments)
        {
            MethodInfo method = type.GetMethod(name, BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance, null, arguments, null);
            if (method == null) throw new InvalidOperationException("Required Revit 2024 API method was not found: " + type.FullName + "." + name);
            return method;
        }

        private static void AssertForgeTypeId(ForgeTypeId expected, ForgeTypeId actual, string label)
        {
            Assert(expected == actual, label + " returned " + (actual?.TypeId ?? "(null)") + ".");
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
