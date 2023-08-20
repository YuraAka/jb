// dotnet create console -n <name>
// dotnet build --output ~/.jb/build /p:BaseIntermediateOutputPath=../../.jb/build/

/*
plan:
    - default target name
    - build exe with sources in subdirs
    - follow recurse if no jb.yaml in children dir
    - deps for libs recursively go to exe
    - auto name exe/lib (lib with prefix)
    - do not process already built items
    - cross-platform home dir obtain
    - restrict commiting symlinks
*/

using System.CommandLine;
using YamlDotNet.Serialization;
using System.Diagnostics;
using SmartFormat;

class TargetSpec
{
    public enum Type
    {
        lib,    ///< most common case, type may be omitted
        exe
    }

    public Type type { get; set; } = Type.lib;
    public string? name { get; set; }
    public string[] deps {get; set; } = new string[0];
}

class CMakeTemplate
{
    public class Context
    {
        public string? ProjectName { get; set; }
        public string? BuildRoot { get; set; }
        public string? SrcRoot { get; set; }

        public string? DepLibNames { get; set; }
        public string? DepLibPaths { get; set; }
    }

    static string ExeTemplate = @"
cmake_minimum_required(VERSION 3.12)
project({ProjectName})
file(GLOB SOURCES _/*.cpp _/*.c)
add_executable({ProjectName} $\{SOURCES\})
find_library(LIB_DEPS
    NAMES {DepLibNames}
    PATHS {DepLibPaths}
)

target_link_libraries(exe2 PRIVATE $\{LIB_DEPS\})
target_include_directories({ProjectName} PUBLIC
    {SrcRoot}
    $\{CMAKE_CURRENT_SOURCE_DIR\}
)
";

    static string LibTemplate = @"
cmake_minimum_required(VERSION 3.12)
project({ProjectName})
file(GLOB SOURCES _/*.cpp _/*.c)

add_library({ProjectName} STATIC $\{SOURCES\})
target_include_directories({ProjectName} PUBLIC {SrcRoot} $\{CMAKE_CURRENT_SOURCE_DIR\})
";

    static public string ComposeExe(Context context)
    {
        return Smart.Format(ExeTemplate, context);
    }

    static public string ComposeLib(Context context)
    {
        return Smart.Format(LibTemplate, context);
    }
}

class Program
{
    const string JbRoot = "/Users/yuraaka/.jb";
    const string BuildRoot = $"{JbRoot}/build";

    static int Main(string[] args)
    {
        var build = new Command("build");
        build.AddAlias("b");
        build.SetHandler((_) => {
            try {
                Build();
            } catch (Exception err) {
                PrintFail(err.Message);
            }
        });

        var clean = new Command("clean");
        clean.SetHandler((_) => { Clean();});

        var root = new RootCommand();
        root.AddCommand(build);
        root.AddCommand(clean);

        return root.Invoke(args);
    }

    static TargetSpec ReadTarget(string path)
    {
        var data = File.ReadAllText(path);
        var deserializer = new DeserializerBuilder().Build();
        var target = deserializer.Deserialize<TargetSpec>(data);
        if (target.name == null) {
            target.name = Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(path)));
        }

        return target;
    }

    static void Build()
    {
        var project = ReadTarget("jb.yaml");
        var srcRoot = FindRepositoryRoot();
        if (srcRoot == null)
        {
            Console.WriteLine("outside repository");
            return;
        }

        var sourceDir = Directory.GetCurrentDirectory();
        BuildTarget(project, srcRoot, sourceDir);
        PrintOk();
    }

    static void PrintOk()
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("Ok");
        Console.ResetColor();
    }

    static void PrintFail(string message)
    {
        Console.WriteLine(message);
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine("Failed");
        Console.ResetColor();
    }

    static void Clean()
    {
        if (Directory.Exists($"{BuildRoot}"))
        {
            Directory.Delete($"{BuildRoot}", true);
        }
    }

    static string MirrorHierarchy(string srcRoot, string fromDir, string toRoot)
    {
        var relPath = Path.GetRelativePath(srcRoot, fromDir);
        var targetPath = Path.Combine(toRoot, relPath);
        if (!Directory.Exists(targetPath))
        {
            Directory.CreateDirectory(targetPath);
        }

        return targetPath;
    }

    static int BuildTarget(TargetSpec target, string srcRoot, string srcDir, bool makeLink = true)
    {
        var buildDir = MirrorHierarchy(srcRoot, srcDir, BuildRoot);
        if (!Directory.Exists($"{buildDir}/_"))
        {
            File.CreateSymbolicLink($"{buildDir}/_", $"{srcDir}");
        }

        GenerateCMakeLists(target, buildDir, srcRoot);
        RunExternal("cmake", ".", buildDir);
        RunExternal("cmake", "--build .", buildDir);
        if (makeLink && target.type == TargetSpec.Type.exe) {
            // todo problems on Windows? project.name.exe
            var binSymlink = Path.Combine(srcDir, $"{target.name}");
            if (File.Exists(binSymlink))
            {
                File.Delete(binSymlink);
            }

            File.CreateSymbolicLink(binSymlink, Path.Combine(buildDir, $"{target.name}"));
        }

        return 0;
    }

    static void GenerateCMakeLists(TargetSpec target, string buildDir, string srcRoot)
    {
        using var writer = File.CreateText($"{buildDir}/CMakeLists.txt");
        var context = new CMakeTemplate.Context
        {
            SrcRoot = srcRoot,
            BuildRoot = BuildRoot,
            ProjectName = target.name
        };

        /// todo: expand to dll
        if (target.deps.Length > 0 && target.type == TargetSpec.Type.exe) {
            var names = new List<string>();
            var paths = new List<string>();
            foreach (var dep in target.deps)
            {
                //var parts = dep.Split('/');
                //var name = parts[parts.Length - 1];
                var depSrcDir = Path.Combine(srcRoot, dep);
                var depTarget = ReadTarget(Path.Combine(depSrcDir, "jb.yaml"));
                names.Add(depTarget.name!);
                paths.Add(Path.Combine(BuildRoot, dep));
                BuildTarget(depTarget, srcRoot, depSrcDir);
            }

            context.DepLibNames = string.Join(" ", names);
            context.DepLibPaths = string.Join(" ", paths);
        }

        switch (target.type)
        {
            case TargetSpec.Type.exe:
                writer.WriteLine(CMakeTemplate.ComposeExe(context));
                break;
            case TargetSpec.Type.lib:
                writer.WriteLine(CMakeTemplate.ComposeLib(context));
                break;
            default:
                throw new ArgumentException($"Unknown type: {target.type}");
        }
    }

    static void RunExternal(string cmd, string args, string wd)
    {
        ProcessStartInfo external = new ProcessStartInfo
        {
            FileName = cmd,
            Arguments = args,
            WorkingDirectory = wd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using (var process = Process.Start(external))
        {
            if (process == null)
            {
                throw new Exception("bad cmd");
            }
            // Read the standard output if needed
            //string output = process.StandardOutput.ReadToEnd();
            //Console.WriteLine("Output:");
            //Console.WriteLine(output);

            // Wait for the process to exit
            process.WaitForExit();
            if (process.ExitCode != 0) {
                var error = process.StandardError.ReadToEnd();
                Console.WriteLine(error);
                throw new Exception($"Failed to run {cmd} {args} from dir {wd}");
            }
        }
    }

    static string? FindRepositoryRoot()
    {
        var currentDir = Directory.GetCurrentDirectory();
        while (!File.Exists(Path.Combine(currentDir, ".jb.root")))
        {
            var parentDir = Directory.GetParent(currentDir);
            if (parentDir == null)
            {
                return null;
            }

            currentDir = parentDir.FullName;
        }

        return currentDir;
    }
}

