using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using VisualXtsi.Classification;
using VisualXtsi.Interop;

namespace VisualXtsi.CSharp
{
    /// <summary>
    /// UnrealBuildTool's rules types and members (ModuleRules, TargetRules, ReadOnlyTargetRules,
    /// UnrealTargetPlatform, ...) from the C# symbol index, from the engine the rules file's project
    /// uses. Build.cs and Target.cs files belong to C++ projects, so Roslyn cannot resolve them; this
    /// stands in for navigation and coloring there.
    /// </summary>
    internal static class UbtSymbols
    {
        // Engine|name -> (category as a type, category as a member, Stamp() when a name was not found:
        // looked up again once the index or the set of rules folders has changed).
        private static readonly ConcurrentDictionary<string, (string Type, string Member, long Stamp)> Categories =
            new ConcurrentDictionary<string, (string, string, long)>(StringComparer.Ordinal);

        private static readonly HashSet<string> RulesClasses = new HashSet<string> { "ModuleRules", "TargetRules", "ReadOnlyTargetRules" };

        /// <summary>Changes whenever the answers may: the symbol count and the rules folders known.</summary>
        public static long Stamp(out int pending)
        {
            Engine.SymbolStatus(out int _, out pending, out int symbols);
            return ((long)UnrealModules.RulesGeneration << 32) | (uint)symbols;
        }

        /// <summary>Definitions named <paramref name="name"/> in the build rules code of <paramref name="file"/>'s engine.</summary>
        public static SymbolInfo[] Definitions(string name, string file)
        {
            IReadOnlyList<string> roots = UnrealModules.RulesRootsFor(file);
            if (roots.Count == 0)
                return Array.Empty<SymbolInfo>();
            return Engine.LookupSymbols(name, 1000, SymbolLanguage.CSharp)
                .Where(s => s.Definition && roots.Any(r => s.Path.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase)))
                .ToArray();
        }

        /// <summary>
        /// The coloring category of a name in a rules file: after a "." a member's if there is one,
        /// elsewhere a type's if there is one; null if the rules code has nothing by that name.
        /// </summary>
        public static string CategoryFor(string name, bool afterDot, string file)
        {
            long stamp = Stamp(out int _);
            string key = UnrealModules.EngineDirectoryFor(file) + "|" + name;
            if (!Categories.TryGetValue(key, out var found) || (found.Type == null && found.Member == null && found.Stamp != stamp))
            {
                var (type, member) = Lookup(name, file);
                Categories[key] = found = (type, member, stamp);
            }
            return afterDot ? found.Member ?? found.Type : found.Type ?? found.Member;
        }

        private static (string Type, string Member) Lookup(string name, string file)
        {
            string type = null, member = null;
            // Members of the rules classes rules files use first ("Logger" is a TargetRules property, and
            // a field elsewhere).
            foreach (SymbolInfo s in Definitions(name, file).OrderBy(s => RulesClasses.Contains(LastScope(s.Scope)) ? 0 : 1))
            {
                switch (s.Kind) // vx_symbol_kind
                {
                    case 2: case 3: case 4: case 5: case 7: type = type ?? SemanticNames.Type; break;
                    case 13: type = type ?? SemanticNames.Delegate; break;
                    case 16: type = type ?? SemanticNames.Interface; break;
                    case 6: member = member ?? SemanticNames.EnumConstant; break;
                    case 8: case 9: member = member ?? SemanticNames.Method; break;
                    case 10: member = member ?? SemanticNames.Field; break;
                    case 14: member = member ?? SemanticNames.Property; break;
                    case 15: member = member ?? SemanticNames.Event; break;
                }
            }
            return (type, member);
        }

        /// <summary>
        /// The definitions of a name used in a rules file, narrowed by where it is used.
        /// "Receiver.Name": members of the receiver's type (Target: ReadOnlyTargetRules in a Build.cs,
        /// TargetRules in a Target.cs; otherwise a type of that name), nothing if there are none (the
        /// receiver is then a variable of a type the rules code does not define, like a List).
        /// A bare name: members of the file's rules class (ModuleRules or TargetRules; not their
        /// constructors), then types of that name, then any definition.
        /// </summary>
        public static IReadOnlyList<SymbolInfo> DefinitionsInContext(string name, string receiver, string file)
        {
            SymbolInfo[] all = Definitions(name, file);
            bool buildCs = file.EndsWith(".Build.cs", StringComparison.OrdinalIgnoreCase);
            if (receiver != null)
            {
                string[] owners = receiver == "Target"
                    ? (buildCs ? new[] { "ReadOnlyTargetRules", "TargetRules" } : new[] { "TargetRules" })
                    : new[] { receiver };
                foreach (string owner in owners)
                {
                    SymbolInfo[] match = all.Where(s => LastScope(s.Scope) == owner).ToArray();
                    if (match.Length > 0)
                        return match;
                }
                return Array.Empty<SymbolInfo>();
            }
            string rulesClass = buildCs ? "ModuleRules" : "TargetRules";
            SymbolInfo[] members = all.Where(s => LastScope(s.Scope) == rulesClass && s.Name != rulesClass).ToArray();
            if (members.Length > 0)
                return members;
            SymbolInfo[] types = all.Where(s => IsType(s.Kind)).ToArray();
            return types.Length > 0 ? types : all;
        }

        private static bool IsType(int kind) => kind == 2 || kind == 3 || kind == 4 || kind == 5 || kind == 7 || kind == 13 || kind == 16;

        private static string LastScope(string scope)
        {
            if (string.IsNullOrEmpty(scope))
                return "";
            int dot = scope.LastIndexOf('.');
            return dot < 0 ? scope : scope.Substring(dot + 1);
        }
    }
}
