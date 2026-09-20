using Autodesk.Revit.Attributes;

namespace SportfyRevit
{
    // The items of the ribbon's "Push to Sportify" drop-down. Each one is a command of its own because Revit binds a ribbon button to a class; all the
    // work is in PushRoofCommandBase, which takes the scope from here. See RoofPushScope for what each part is.

    /// <summary>Everything the model knows about the roof: outline and size, structure, entries, openings, edge and walls, drains, equipment, slab and levels.</summary>
    [Transaction(TransactionMode.ReadOnly)]
    public class PushRoofBoundaryCommand : PushRoofCommandBase
    {
        internal override RoofPushScope Scope => RoofPushScope.All;
    }

    /// <summary>Only the roof's outline, size and height above ground (the plan's frame).</summary>
    [Transaction(TransactionMode.ReadOnly)]
    public class PushRoofOutlineCommand : PushRoofCommandBase
    {
        internal override RoofPushScope Scope => RoofPushScope.Roof;
    }

    /// <summary>The structure under the roof: grid lines, columns, beams, bearing walls.</summary>
    [Transaction(TransactionMode.ReadOnly)]
    public class PushRoofStructureCommand : PushRoofCommandBase
    {
        internal override RoofPushScope Scope => RoofPushScope.Structure;
    }

    /// <summary>Stairs, lifts and doors by which people reach the roof.</summary>
    [Transaction(TransactionMode.ReadOnly)]
    public class PushRoofEntriesCommand : PushRoofCommandBase
    {
        internal override RoofPushScope Scope => RoofPushScope.Entries;
    }

    /// <summary>Holes in the roof: skylights, shafts, rooflights.</summary>
    [Transaction(TransactionMode.ReadOnly)]
    public class PushRoofOpeningsCommand : PushRoofCommandBase
    {
        internal override RoofPushScope Scope => RoofPushScope.Openings;
    }

    /// <summary>Parapets and railings along the edge, and the walls standing on the roof.</summary>
    [Transaction(TransactionMode.ReadOnly)]
    public class PushRoofEdgeCommand : PushRoofCommandBase
    {
        internal override RoofPushScope Scope => RoofPushScope.Edge;
    }

    /// <summary>Roof drains, overflows and scuppers.</summary>
    [Transaction(TransactionMode.ReadOnly)]
    public class PushRoofDrainsCommand : PushRoofCommandBase
    {
        internal override RoofPushScope Scope => RoofPushScope.Drains;
    }

    /// <summary>Plant and equipment standing on the roof.</summary>
    [Transaction(TransactionMode.ReadOnly)]
    public class PushRoofEquipmentCommand : PushRoofCommandBase
    {
        internal override RoofPushScope Scope => RoofPushScope.Equipment;
    }

    /// <summary>The slab's build-up (its structural thickness is what the resonance estimate needs) and the levels.</summary>
    [Transaction(TransactionMode.ReadOnly)]
    public class PushRoofSlabLevelsCommand : PushRoofCommandBase
    {
        internal override RoofPushScope Scope => RoofPushScope.SlabLevels;
    }
}
