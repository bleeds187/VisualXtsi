using System;
using System.Collections.Generic;

namespace VisualXtsi.QuickInfo
{
    /// <summary>One-line descriptions of common Unreal reflection specifiers, for QuickInfo.</summary>
    internal static class UeSpecifiers
    {
        private static readonly Dictionary<string, string> Descriptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // Properties
            ["EditAnywhere"] = "Editable in the Details panel on both class defaults and placed instances.",
            ["EditDefaultsOnly"] = "Editable on class defaults (Blueprint and archetypes) but not on placed instances.",
            ["EditInstanceOnly"] = "Editable on placed instances but not on class defaults.",
            ["VisibleAnywhere"] = "Shown read-only in the Details panel everywhere.",
            ["VisibleDefaultsOnly"] = "Shown read-only on class defaults only.",
            ["VisibleInstanceOnly"] = "Shown read-only on placed instances only.",
            ["BlueprintReadWrite"] = "Blueprint graphs can get and set this property.",
            ["BlueprintReadOnly"] = "Blueprint graphs can get, but not set, this property.",
            ["BlueprintAssignable"] = "A multicast delegate that Blueprints can bind events to.",
            ["BlueprintCallable"] = "On a function: callable from Blueprint graphs. On a delegate property: Blueprints can broadcast it.",
            ["Category"] = "Groups the item in the editor (Details panel, Blueprint menus); \"A|B\" nests subcategories.",
            ["Replicated"] = "The server sends changes of this property to clients.",
            ["ReplicatedUsing"] = "Replicated, and the named OnRep function runs on clients when a new value arrives.",
            ["NotReplicated"] = "Excluded from replication inside a replicated struct.",
            ["Transient"] = "Not saved or loaded; starts zeroed.",
            ["Config"] = "Read from and saved to the class's .ini configuration.",
            ["GlobalConfig"] = "Like Config, but always the base class's section, never overridden by subclasses.",
            ["SaveGame"] = "Included when the object is serialized for a save game (ArIsSaveGame).",
            ["Instanced"] = "Object reference that gets its own copy (subobject) per owning instance.",
            ["Export"] = "Object reference whose object is exported in full when copying or exporting.",
            ["Interp"] = "Can be keyed and animated in Sequencer.",
            ["AdvancedDisplay"] = "Shown in the Details panel's collapsed advanced section.",
            ["SimpleDisplay"] = "Always shown in the Details panel, never under advanced.",
            ["NoClear"] = "The editor hides the option to clear this reference to None.",
            ["meta"] = "Metadata keys for editor and tooling behavior (display names, clamps, conditions, ...).",
            // Functions
            ["BlueprintPure"] = "Callable from Blueprints as a pure node: no execution pins, should not change state.",
            ["BlueprintImplementableEvent"] = "Declared in C++, implemented only in Blueprints (no C++ body).",
            ["BlueprintNativeEvent"] = "Overridable in Blueprints with a C++ default: write the body as Name_Implementation.",
            ["BlueprintAuthorityOnly"] = "Runs only on the machine with network authority.",
            ["BlueprintCosmetic"] = "Cosmetic only: does not run on dedicated servers.",
            ["CallInEditor"] = "Shows a button in the Details panel that calls the function in the editor.",
            ["Exec"] = "Callable from the in-game console.",
            ["Server"] = "RPC: called on a client, runs on the server. Write the body as Name_Implementation.",
            ["Client"] = "RPC: called on the server, runs on the owning client. Write the body as Name_Implementation.",
            ["NetMulticast"] = "RPC: called on the server, runs on the server and all clients. Write the body as Name_Implementation.",
            ["Reliable"] = "RPC that is delivered and in order, resent if lost.",
            ["Unreliable"] = "RPC that may be dropped under load; for frequent, cosmetic updates.",
            ["WithValidation"] = "RPC with a Name_Validate check; returning false disconnects the caller.",
            ["SealedEvent"] = "An event that cannot be overridden in subclasses.",
            // Types
            ["BlueprintType"] = "The type can be used for Blueprint variables.",
            ["NotBlueprintType"] = "The type cannot be used for Blueprint variables.",
            ["Blueprintable"] = "Blueprints can subclass this class.",
            ["NotBlueprintable"] = "Blueprints cannot subclass this class.",
            ["Abstract"] = "The class cannot be instantiated or placed; only subclassed.",
            ["MinimalAPI"] = "Exports only the type information (casts work) but not the functions from the module.",
            ["Within"] = "Objects of this class can only exist with an outer of the named class.",
            ["DefaultToInstanced"] = "Properties of this type are Instanced by default.",
            ["EditInlineNew"] = "Objects of this class can be created inline from the Details panel.",
            ["ClassGroup"] = "Groups the class in the editor's Add Component list.",
            ["HideCategories"] = "Hides the listed property categories in the Details panel.",
            ["ShowCategories"] = "Shows listed categories that a parent class hid.",
            ["Atomic"] = "The struct is always serialized as a whole, not property by property.",
            ["Immutable"] = "The struct is serialized as a whole and cannot be changed in the editor.",
            ["Bitflags"] = "On an enum: its values can be combined as bit flags (with meta UseEnumValuesAsMaskValuesInEditor).",
            // Common meta keys
            ["DisplayName"] = "The name the editor shows instead of the C++ name.",
            ["ToolTip"] = "The tooltip the editor shows (instead of the comment above the declaration).",
            ["ClampMin"] = "Smallest value accepted.",
            ["ClampMax"] = "Largest value accepted.",
            ["UIMin"] = "Smallest value of the slider (typing can go below it).",
            ["UIMax"] = "Largest value of the slider (typing can go above it).",
            ["EditCondition"] = "Editable only while the named condition is true.",
            ["EditConditionHides"] = "Hidden, not just disabled, while EditCondition is false.",
            ["AllowPrivateAccess"] = "Lets Blueprint specifiers apply to a private C++ member.",
            ["ExposeOnSpawn"] = "Shown as a pin on Blueprint spawn and construct nodes.",
            ["Units"] = "The unit shown next to the value (cm, s, deg, ...).",
            ["Hidden"] = "On an enum value: not shown in the editor.",
        };

        /// <summary>The description of a specifier; null if it is not a well-known one.</summary>
        public static string Describe(string name) => Descriptions.TryGetValue(name, out string text) ? text : null;
    }
}
