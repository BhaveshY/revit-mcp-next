using Autodesk.Revit.DB;

namespace RevitMcpNext.Addin.Revit
{
    /// <summary>
    /// Legacy placement queries kept from the old Ops.FamilyPlacement.cs because files outside the modeling lane still
    /// call them (Reads/Handler.Catalog.cs, Reads/Handler.ModelHealth.cs, Serialization/Handler.Snapshots.cs). SPEC §9.1:
    /// a lane deletes legacy members only when nothing outside its files references them; the W3 legacy sweep removes
    /// these once those callers are rewritten.
    /// </summary>
    internal sealed partial class RevitExternalEventHandler
    {
        private static string GetPlacementType(Element element)
        {
            FamilySymbol symbol = element as FamilySymbol;
            return symbol?.Family?.FamilyPlacementType.ToString() ?? string.Empty;
        }

        private static bool IsSupportedWallHostedFamilySymbol(FamilySymbol symbol)
        {
            return symbol != null &&
                   symbol.Family != null &&
                   symbol.Family.FamilyPlacementType == FamilyPlacementType.OneLevelBasedHosted &&
                   IsWallHostedDoorWindowCategory(symbol);
        }

        private static bool IsSupportedLevelBasedFamilySymbol(FamilySymbol symbol)
        {
            return symbol != null &&
                   symbol.Family != null &&
                   symbol.Family.FamilyPlacementType == FamilyPlacementType.OneLevelBased &&
                   IsLevelBasedFurnitureEquipmentFixtureCategory(symbol);
        }

        private static bool IsWallHostedDoorWindowCategory(FamilySymbol symbol)
        {
            return IsBuiltInCategory(symbol, BuiltInCategory.OST_Doors, BuiltInCategory.OST_Windows);
        }

        private static bool IsLevelBasedFurnitureEquipmentFixtureCategory(FamilySymbol symbol)
        {
            return IsBuiltInCategory(
                symbol,
                BuiltInCategory.OST_Furniture,
                BuiltInCategory.OST_FurnitureSystems,
                BuiltInCategory.OST_ElectricalEquipment,
                BuiltInCategory.OST_MechanicalEquipment,
                BuiltInCategory.OST_PlumbingFixtures,
                BuiltInCategory.OST_ElectricalFixtures,
                BuiltInCategory.OST_LightingFixtures,
                BuiltInCategory.OST_SpecialityEquipment);
        }
    }
}
