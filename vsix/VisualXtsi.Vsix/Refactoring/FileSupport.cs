using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace VisualXtsi.Refactoring
{
    /// <summary>Create File, Move Class to New File and Rename Files, on plain strings and paths.</summary>
    internal static class FileSupport
    {
        /// <summary>The kinds of class Create File writes, with the prefix their names take.</summary>
        public static readonly (string Kind, string Prefix)[] Templates =
        {
            ("Plain class", "F"), ("UObject", "U"), ("AActor", "A"), ("UActorComponent", "U"), ("USTRUCT", "F"), ("UINTERFACE", "U"),
        };

        /// <summary>A class's file name: its name without the Unreal prefix ("AMyActor" -> "MyActor").</summary>
        public static string BaseName(string className) =>
            className.Length > 1 && "UAFSIE".IndexOf(className[0]) >= 0 && char.IsUpper(className[1]) ? className.Substring(1) : className;

        /// <summary>A name with the prefix the kind of class takes ("MyActor" -> "AMyActor").</summary>
        public static string ClassName(string name, string prefix) =>
            name.Length > 1 && name[0] == prefix[0] && char.IsUpper(name[1]) ? name : prefix + BaseName(name);

        /// <summary>Why a class name cannot be used (null: it can).</summary>
        public static string ValidateClassName(string name) =>
            string.IsNullOrWhiteSpace(name) ? "Type a class name." :
            !Regex.IsMatch(name, @"^[A-Za-z_][A-Za-z0-9_]*$") ? "A class name has letters, digits and underscores." : null;

        /// <summary>The comment new files start with, from DefaultGame.ini's CopyrightNotice ("" if none).</summary>
        public static string CopyrightFrom(string defaultGameIni)
        {
            if (string.IsNullOrEmpty(defaultGameIni))
                return "";
            Match m = Regex.Match(defaultGameIni, @"^\s*CopyrightNotice\s*=\s*(.*?)\s*$", RegexOptions.Multiline);
            if (!m.Success)
                return "";
            string notice = m.Groups[1].Value.Trim();
            if (notice.Length >= 2 && notice[0] == '"' && notice[notice.Length - 1] == '"')
                notice = notice.Substring(1, notice.Length - 2).Replace("\\\"", "\"");
            if (notice.Length == 0)
                return "";
            return notice.StartsWith("//") || notice.StartsWith("/*") ? notice : "// " + notice;
        }

        /// <summary>
        /// Where a new class's files go in a module (the folder with its Build.cs): the header under
        /// Public (or Classes) and the source under Private, in the same subfolder as
        /// <paramref name="near"/> has there; without Public/Private, both next to <paramref name="near"/>.
        /// </summary>
        public static (string HeaderDir, string SourceDir) Layout(string moduleRoot, string near, Func<string, bool> exists)
        {
            string here = Path.GetDirectoryName(near);
            if (moduleRoot == null)
                return (here, here);
            string pub = Path.Combine(moduleRoot, "Public"), priv = Path.Combine(moduleRoot, "Private"), classes = Path.Combine(moduleRoot, "Classes");
            if (!exists(priv) || !(exists(pub) || exists(classes)))
                return (here, here);
            string headers = exists(pub) ? pub : classes;
            string sub = "";
            foreach (string root in new[] { pub, priv, classes })
                if (here.StartsWith(root, StringComparison.OrdinalIgnoreCase) && (here.Length == root.Length || here[root.Length] == '\\'))
                    sub = here.Substring(root.Length).TrimStart('\\');
            return (sub.Length == 0 ? headers : Path.Combine(headers, sub), sub.Length == 0 ? priv : Path.Combine(priv, sub));
        }

        /// <summary>
        /// The header and source of a new class of a kind (see <see cref="Templates"/>): reflected kinds
        /// with their .generated.h, GENERATED_BODY() and the module's export macro; tabs, as Unreal's
        /// code has them. The source includes the header by <paramref name="headerInclude"/>.
        /// </summary>
        public static (string Header, string Source) Template(string kind, string className, string apiMacro, string headerInclude, string copyright, string nl)
        {
            string api = string.IsNullOrEmpty(apiMacro) ? "" : apiMacro + " ";
            string baseName = BaseName(className);
            var h = new StringBuilder();
            var s = new StringBuilder();
            if (copyright.Length > 0)
            {
                h.Append(copyright).Append(nl).Append(nl);
                s.Append(copyright).Append(nl).Append(nl);
            }
            h.Append("#pragma once").Append(nl).Append(nl).Append("#include \"CoreMinimal.h\"").Append(nl);
            s.Append("#include \"").Append(headerInclude).Append('"').Append(nl);
            string generated = "#include \"" + baseName + ".generated.h\"" + nl;
            switch (kind)
            {
                case "UObject":
                    h.Append("#include \"UObject/Object.h\"").Append(nl).Append(generated).Append(nl)
                     .Append("UCLASS()").Append(nl).Append("class ").Append(api).Append(className).Append(" : public UObject").Append(nl)
                     .Append('{').Append(nl).Append("\tGENERATED_BODY()").Append(nl).Append("};").Append(nl);
                    break;
                case "AActor":
                    h.Append("#include \"GameFramework/Actor.h\"").Append(nl).Append(generated).Append(nl)
                     .Append("UCLASS()").Append(nl).Append("class ").Append(api).Append(className).Append(" : public AActor").Append(nl)
                     .Append('{').Append(nl).Append("\tGENERATED_BODY()").Append(nl).Append(nl)
                     .Append("public:").Append(nl).Append('\t').Append(className).Append("();").Append(nl).Append(nl)
                     .Append("\tvirtual void Tick(float DeltaTime) override;").Append(nl).Append(nl)
                     .Append("protected:").Append(nl).Append("\tvirtual void BeginPlay() override;").Append(nl).Append("};").Append(nl);
                    s.Append(nl).Append(className).Append("::").Append(className).Append("()").Append(nl).Append('{').Append(nl)
                     .Append("\tPrimaryActorTick.bCanEverTick = true;").Append(nl).Append('}').Append(nl).Append(nl)
                     .Append("void ").Append(className).Append("::BeginPlay()").Append(nl).Append('{').Append(nl).Append("\tSuper::BeginPlay();").Append(nl).Append('}').Append(nl).Append(nl)
                     .Append("void ").Append(className).Append("::Tick(float DeltaTime)").Append(nl).Append('{').Append(nl).Append("\tSuper::Tick(DeltaTime);").Append(nl).Append('}').Append(nl);
                    break;
                case "UActorComponent":
                    h.Append("#include \"Components/ActorComponent.h\"").Append(nl).Append(generated).Append(nl)
                     .Append("UCLASS(ClassGroup = (Custom), meta = (BlueprintSpawnableComponent))").Append(nl)
                     .Append("class ").Append(api).Append(className).Append(" : public UActorComponent").Append(nl)
                     .Append('{').Append(nl).Append("\tGENERATED_BODY()").Append(nl).Append(nl)
                     .Append("public:").Append(nl).Append('\t').Append(className).Append("();").Append(nl).Append(nl)
                     .Append("\tvirtual void TickComponent(float DeltaTime, ELevelTick TickType, FActorComponentTickFunction* ThisTickFunction) override;").Append(nl).Append(nl)
                     .Append("protected:").Append(nl).Append("\tvirtual void BeginPlay() override;").Append(nl).Append("};").Append(nl);
                    s.Append(nl).Append(className).Append("::").Append(className).Append("()").Append(nl).Append('{').Append(nl)
                     .Append("\tPrimaryComponentTick.bCanEverTick = true;").Append(nl).Append('}').Append(nl).Append(nl)
                     .Append("void ").Append(className).Append("::BeginPlay()").Append(nl).Append('{').Append(nl).Append("\tSuper::BeginPlay();").Append(nl).Append('}').Append(nl).Append(nl)
                     .Append("void ").Append(className).Append("::TickComponent(float DeltaTime, ELevelTick TickType, FActorComponentTickFunction* ThisTickFunction)").Append(nl)
                     .Append('{').Append(nl).Append("\tSuper::TickComponent(DeltaTime, TickType, ThisTickFunction);").Append(nl).Append('}').Append(nl);
                    break;
                case "USTRUCT":
                    h.Append(generated).Append(nl).Append("USTRUCT(BlueprintType)").Append(nl).Append("struct ").Append(api).Append(className).Append(nl)
                     .Append('{').Append(nl).Append("\tGENERATED_BODY()").Append(nl).Append("};").Append(nl);
                    break;
                case "UINTERFACE":
                    string iname = "I" + baseName;
                    h.Append("#include \"UObject/Interface.h\"").Append(nl).Append(generated).Append(nl)
                     .Append("UINTERFACE(MinimalAPI)").Append(nl).Append("class ").Append(className).Append(" : public UInterface").Append(nl)
                     .Append('{').Append(nl).Append("\tGENERATED_BODY()").Append(nl).Append("};").Append(nl).Append(nl)
                     .Append("class ").Append(api).Append(iname).Append(nl).Append('{').Append(nl).Append("\tGENERATED_BODY()").Append(nl).Append(nl)
                     .Append("public:").Append(nl).Append("};").Append(nl);
                    break;
                default:
                    h.Append(nl).Append("class ").Append(api).Append(className).Append(nl).Append('{').Append(nl)
                     .Append("public:").Append(nl).Append('\t').Append(className).Append("();").Append(nl)
                     .Append("\t~").Append(className).Append("();").Append(nl).Append("};").Append(nl);
                    s.Append(nl).Append(className).Append("::").Append(className).Append("()").Append(nl).Append('{').Append(nl).Append('}').Append(nl).Append(nl)
                     .Append(className).Append("::~").Append(className).Append("()").Append(nl).Append('{').Append(nl).Append('}').Append(nl);
                    break;
            }
            return (h.ToString(), s.ToString());
        }

        /// <summary>
        /// An #include line naming <paramref name="oldFile"/> as its last path part, made to name
        /// <paramref name="newFile"/>; null when the line includes something else.
        /// </summary>
        public static string RewriteInclude(string line, string oldFile, string newFile)
        {
            Match m = Regex.Match(line, @"^(\s*#\s*include\s*[""<])([^"">]*)([>""].*)$");
            if (!m.Success)
                return null;
            string path = m.Groups[2].Value;
            int slash = path.LastIndexOfAny(new[] { '/', '\\' });
            string name = path.Substring(slash + 1);
            if (!string.Equals(name, oldFile, StringComparison.OrdinalIgnoreCase))
                return null;
            return m.Groups[1].Value + path.Substring(0, slash + 1) + newFile + m.Groups[3].Value;
        }

        /// <summary>Whether an include path (its folders and name) can be the file at <paramref name="fullPath"/>.</summary>
        public static bool IncludeMatches(string includePath, string fullPath)
        {
            string normalized = includePath.Replace('/', '\\').TrimStart('.', '\\');
            return fullPath.EndsWith("\\" + normalized, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(fullPath, normalized, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// A class's whole text in a header: from its doc comment and reflection macro above (if any)
        /// to the line after its closing "};". <paramref name="classStart"/> is where the class's
        /// declaration starts, <paramref name="classEnd"/> after its "}".
        /// </summary>
        public static (int Start, int End) ClassBlock(string text, int classStart, int classEnd)
        {
            int start = TextScan.LineStart(text, classStart);
            // The reflection macro (possibly over several lines), then the comments right above.
            string macro = RenameSupport.ReflectionMacro(text, classStart);
            int at = macro == null ? -1 : text.LastIndexOf(macro + "(", classStart, StringComparison.Ordinal);
            if (at >= 0)
                start = TextScan.LineStart(text, at);
            while (start > 0)
            {
                int prev = TextScan.LineStart(text, start - 1);
                string content = text.Substring(prev, start - prev).Trim();
                if (!(content.StartsWith("//") || content.StartsWith("/*") || content.StartsWith("*")))
                    break;
                start = prev;
            }
            int end = classEnd;
            while (end < text.Length && (text[end] == ' ' || text[end] == '\t'))
                end++;
            if (end < text.Length && text[end] == ';')
                end++;
            end = TextScan.NextLineStart(text, end);
            return (start, end);
        }

        /// <summary>The #include lines of a file, in order (whole lines, without their line breaks).</summary>
        public static List<string> Includes(string text) =>
            Regex.Matches(text, @"^[ \t]*#[ \t]*include[ \t]*[""<][^"">\r\n]*["">][^\r\n]*", RegexOptions.Multiline).Cast<Match>().Select(m => m.Value.Trim()).ToList();

        /// <summary>The names of the types and namespaces a header declares (classes, structs, enums, aliases), declared ahead or not.</summary>
        public static HashSet<string> DeclaredNames(string text)
        {
            var names = new HashSet<string>();
            string code = Regex.Replace(text, @"//[^\n]*|/\*.*?\*/", "", RegexOptions.Singleline);
            foreach (Match m in Regex.Matches(code, @"\b(?:class|struct|union|enum(?:\s+class|\s+struct)?|namespace)\s+(?:[A-Z][A-Z0-9_]*_API\s+)?([A-Za-z_]\w*)"))
                names.Add(m.Groups[1].Value);
            foreach (Match m in Regex.Matches(code, @"\busing\s+([A-Za-z_]\w*)\s*=|\btypedef\b[^;{}]*?\b([A-Za-z_]\w*)\s*;"))
                names.Add(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
            return names;
        }

        /// <summary>The names in <paramref name="names"/> that <paramref name="code"/> uses (whole words, comments aside).</summary>
        public static List<string> Uses(string code, IEnumerable<string> names)
        {
            string stripped = Regex.Replace(code, @"//[^\n]*|/\*.*?\*/", "", RegexOptions.Singleline);
            return names.Where(n => Regex.IsMatch(stripped, @"\b" + Regex.Escape(n) + @"\b")).ToList();
        }

        /// <summary>Whether a header still declares Unreal reflected types (it then keeps its .generated.h).</summary>
        public static bool HasReflectedTypes(string text) =>
            Regex.IsMatch(text, @"^\s*(UCLASS|USTRUCT|UENUM|UINTERFACE|UDELEGATE)\s*\(|DECLARE_DYNAMIC_\w*DELEGATE", RegexOptions.Multiline);
    }
}
