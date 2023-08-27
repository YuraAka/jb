// dotnet create console -n <name>
// dotnet build --output ~/.jb/build /p:BaseIntermediateOutputPath=../../.jb/build/

/*
plan:
    - !!support boost (external libs download via cmake fetch)
        - cmake fetch
        - conan https://github.com/conan-community/conan-boost
            - how to deal with deps:
                - special syntax: @boost
                - ordinary path to some folder with conanfile.txt or so
    - auto name exe/lib (lib with prefix)
    - do not process already built items
    - cross-platform home dir obtain
    - restrict commiting symlinks
*/

using System.CommandLine;
using YamlDotNet.Serialization;
using System.Diagnostics;
using SmartFormat;

class ProjectSpec
{
    public enum Type
    {
        lib,    ///< most common case, type may be omitted
        exe,
        ext     ///< external dependency (package)
    }

    public enum Vendor
    {
        conan
    }

    public Type type { get; set; } = Type.lib;
    public string? name { get; set; }

    /// <summary>
    ///  Conan uses alternative names (aliases) for CMake
    /// </summary>
    public string? alias { get; set; }
    public string[] deps {get; set; } = new string[0];

    public string? version { get; set; }

    public Vendor? vendor{ get; set; }
}

class CMakeTemplate
{
    public class Context
    {
        public string? ProjectName { get; set; }
        public string? BuildRoot { get; set; }
        public string? SrcRoot { get; set; }

        public string? SrcPaths { get; set; }

        public string? DepLibNames { get; set; }
        public string? DepLibPaths { get; set; }

        public string? DepPackageNames { get; set; }
        public string? DepPackagePaths { get; set; }
    }

    static string ExeTemplate = @"
cmake_minimum_required(VERSION 3.12)
project({ProjectName})
file(GLOB SOURCES {SrcPaths})

add_executable({ProjectName} $\{SOURCES\})

set(lib_names {DepLibNames})
set(lib_paths {DepLibPaths})
set(LIB_DEPS)

foreach(name path IN ZIP_LISTS lib_names lib_paths)
    find_library(LIB_$\{name\}
        NAMES $\{name\}
        PATHS $\{path\}
        NO_DEFAULT_PATH
    )

    if (LIB_$\{name\})
        list(APPEND LIB_DEPS $\{LIB_$\{name\}\})
    else()
        message(FATAL_ERROR ""$\{name\} not found in path $\{path\}."")
    endif()
endforeach()

set(pkg_names {DepPackageNames})
set(pkg_paths {DepPackagePaths})
set(CMAKE_PREFIX_PATH {DepPackagePaths})

foreach(name path IN ZIP_LISTS pkg_names pkg_paths)
    find_package(
        $\{name\} REQUIRED
        PATHS $\{path\}
        NO_DEFAULT_PATH
    )
    if (name)
        list(APPEND LIB_DEPS $\{name\}::$\{name\})
    else()
        message(FATAL_ERROR ""$\{name\} not found in path $\{path\}."")
    endif()
endforeach()

target_link_libraries({ProjectName} PRIVATE $\{LIB_DEPS\})
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

class ConanTemplate
{
    public class Context
    {
        public string? Package { get; set; }
        public string? Version { get; set; }
    }

    static string PackageTemplate = @"
[requires]
{Package}/{Version}

[generators]
CMakeDeps
CMakeToolchain
";

    static public string Compose(Context context)
    {
        return Smart.Format(PackageTemplate, context);
    }
}

class Env
{
    public string BuildRoot { get { return $"{ServiceDir}/build"; } }
    public readonly string SourceRoot;

    public static Env CreateOnHome()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new Env(Path.Combine(home, ".jb"));
    }

    public Env(string serviceDir)
    {
        ServiceDir = serviceDir;
        SourceRoot = GetSourceRoot();
    }

    public string GetProjectPath(string projectDir)
    {
        return Path.Combine(projectDir, ProjectConfigFileName);
    }

    public string GetProjectDirectory(string curDir)
    {
        string cfgPath = GetProjectPath(curDir);
        if (Path.Exists(cfgPath))
        {
            return curDir;
        }

        if (curDir == SourceRoot)
        {
            throw new Exception($"Cannot locate {ProjectConfigFileName} in parent hierarchy");
        }

        return GetProjectDirectory(Directory.GetParent(curDir)!.FullName);
    }

    public string GetAbsoluteSourcePath(string srcRootRelativePath)
    {
        return Path.Combine(SourceRoot, srcRootRelativePath);
    }

    public string GetRelativeSourcePath(string srcRootAbsPath)
    {
        return Path.GetRelativePath(SourceRoot, srcRootAbsPath);
    }

    public string GetBuildPath(string srcRelPath)
    {
        return Path.Combine(BuildRoot, srcRelPath);
    }

    public string MirrorSourceToBuild(string srcRelDir)
    {
        var targetPath = GetBuildPath(srcRelDir);
        if (!Directory.Exists(targetPath))
        {
            Directory.CreateDirectory(targetPath);
        }

        return targetPath;
    }

    string GetSourceRoot()
    {
        var currentDir = Directory.GetCurrentDirectory();
        while (!File.Exists(Path.Combine(currentDir, ".jb.root")))
        {
            var parentDir = Directory.GetParent(currentDir);
            if (parentDir == null)
            {
                throw new Exception("Outside of repository");
            }

            currentDir = parentDir.FullName;
        }

        return currentDir;
    }

    const string ProjectConfigFileName = "jb.yaml";

    readonly string ServiceDir;
}

class BuildNode
{
    public readonly string Path;

    public readonly string SourcePath;
    public readonly string BuildPath;
    public readonly ProjectSpec Project;

    public readonly string Name;
    public readonly string Alias;

    public List<BuildNode> Dependers;

    public List<BuildNode> Dependees;

    public BuildNode(string path, Env env)
    {
        Path = path;
        SourcePath = env.GetAbsoluteSourcePath(path);
        BuildPath = env.GetBuildPath(path);
        var projectFilePath = env.GetProjectPath(SourcePath);
        Dependers = new List<BuildNode>();
        Dependees = new List<BuildNode>();
        var data = File.ReadAllText(projectFilePath);
        var deserializer = new DeserializerBuilder().Build();
        Project = deserializer.Deserialize<ProjectSpec>(data);
        Name = Project.name != null
            ? Project.name
            : Project.name = System.IO.Path.GetFileName(path);
        Alias = Project.alias != null
            ? Project.alias
            : Name;
    }

    public List<BuildNode> CollectTransitiveLibraryDependers()
    {
        var result = new List<BuildNode>();
        var used = new HashSet<string>();
        result.Add(this);

        int i = 0;
        while (i < result.Count)
        {
            foreach(var dep in result[i].Dependers)
            {
                if (dep.Project.type != ProjectSpec.Type.exe && !used.Contains(dep.Path))
                {
                    used.Add(dep.Path);
                    result.Add(dep);
                }
            }

            ++i;
        }

        return result.GetRange(1, result.Count - 1);
    }
}

class BuildGraph
{
    public delegate void Visitor(BuildNode node);

    public BuildGraph(string startDir, Env env)
    {
        var targetDep = new Dictionary<string, string>();
        var entryProjectDir = env.GetProjectDirectory(startDir);
        var srcDir = env.GetRelativeSourcePath(entryProjectDir);
        var entryNode = new BuildNode(srcDir, env);
        var stack = new Stack<BuildNode>();
        var topoRoots = new Queue<BuildNode>();
        stack.Push(entryNode);
        while (stack.Count > 0)
        {
            var targetNode = stack.Pop();
            foreach(var depPath in targetNode.Project.deps)
            {
                var depNode = new BuildNode(depPath, env);
                if (targetDep.ContainsKey(depNode.Path)) {
                    /// todo print full cycle
                    throw new Exception($"Cycle detected: {depNode.Path}");
                } else {
                    targetDep[targetNode.Path] = depNode.Path;
                }

                targetNode.Dependers.Add(depNode);
                depNode.Dependees.Add(targetNode);
                stack.Push(depNode);
            }

            if (targetNode.Dependers.Count == 0)
            {
                topoRoots.Enqueue(targetNode);
            }
        }

        TopoNodes = TopoSort(topoRoots);
    }

    public void Traverse(Visitor visitor)
    {
        foreach(var node in TopoNodes)
        {
            visitor(node);
        }
    }

    static List<BuildNode> TopoSort(Queue<BuildNode> queue)
    {
        var result = new List<BuildNode>();
        var inDegree = new Dictionary<string, int>();
        while(queue.Count > 0)
        {
            var node = queue.Dequeue();
            result.Add(node);
            foreach (var dep in node.Dependees) {
                if (!inDegree.ContainsKey(dep.Path))
                {
                    inDegree[dep.Path] = dep.Dependers.Count;
                }

                if (--inDegree[dep.Path] == 0)
                {
                    queue.Enqueue(dep);
                    inDegree.Remove(dep.Path);
                }
            }
        }

        return result;
    }

    readonly List<BuildNode> TopoNodes;
}

class Program
{
    const string JbRoot = "/Users/yuraaka/.jb";
    const string BuildRoot = $"{JbRoot}/build";
    const string ProjectConfigFileName = "jb.yaml";

    static int Main(string[] args)
    {
        var verbose = new Option<bool>("--verbose", "Enable verbose output");
        var build = new Command("build");
        build.AddAlias("b");
        build.AddOption(verbose);
        build.SetHandler((verbose) => {
            try {
                Build(verbose);
            } catch (Exception err) {
                PrintFail(err.Message);
            }
        }, verbose);

        var clean = new Command("clean");
        clean.SetHandler((_) => { Clean();});

        var graph = new Command("graph");
        graph.SetHandler((_) => {
            var env = Env.CreateOnHome();
            var curDir = Directory.GetCurrentDirectory();
            var graph = new BuildGraph(curDir, env);
            graph.Traverse(node => {
                Console.WriteLine(node.Path);
            });
        });

        var root = new RootCommand();
        root.AddCommand(build);
        root.AddCommand(clean);
        root.AddCommand(graph);

        return root.Invoke(args);
    }

    static void Build(bool verbose)
    {
        var env = Env.CreateOnHome();
        var curDir = Directory.GetCurrentDirectory();
        var graph = new BuildGraph(curDir, env);
        graph.Traverse(node => {
            var buildDir = env.MirrorSourceToBuild(node.Path);
            if (node.Project.type == ProjectSpec.Type.ext)
            {
                BuildExternal(node);
                RunExternal("conan", "install . --build=missing", buildDir, verbose);
                return;
            }


            var srcDir = env.GetAbsoluteSourcePath(node.Path);
            if (!Directory.Exists($"{buildDir}/_"))
            {
                File.CreateSymbolicLink($"{buildDir}/_", $"{srcDir}");
            }

            Prepare(node, env);
            RunExternal("cmake", ". -DCMAKE_BUILD_TYPE=Release", buildDir, verbose);
            RunExternal("cmake", "--build .", node.BuildPath, verbose);
            if (node.Project.type == ProjectSpec.Type.exe)
            {
                // todo problems on Windows? project.name.exe
                var binSymlink = Path.Combine(node.SourcePath, $"{node.Project.name}");
                if (File.Exists(binSymlink))
                {
                    File.Delete(binSymlink);
                }

                File.CreateSymbolicLink(binSymlink, Path.Combine(node.BuildPath, $"{node.Project.name}"));
            }
        });

        PrintOk();
    }

    static void BuildExternal(BuildNode node)
    {
        if (node.Project.vendor == ProjectSpec.Vendor.conan)
        {
            using var writer = File.CreateText($"{node.BuildPath}/conanfile.txt");
            var conanfile = ConanTemplate.Compose(new ConanTemplate.Context()
            {
                Package = node.Project.name!,
                Version = node.Project.version!,
            });

            writer.WriteLine(conanfile);
        }

        // generate conanfile.txt
        //RunExternal("cmake", ".", buildDir, verbose);
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
    static bool IsSymbolicLink(string directoryPath)
    {
        FileAttributes attributes = File.GetAttributes(directoryPath);
        return (attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint;
    }

    static List<string> CollectProjectSubdirs(string srcDir, bool top)
    {
        var result = new List<string>(){srcDir};
        foreach (string subDir in Directory.GetDirectories(srcDir))
        {
            var path = Path.Combine(srcDir, subDir);
            if (IsSymbolicLink(path)) {
                // does not follow symlinks
                continue;
            }

            if (top || !File.Exists(Path.Combine(path, ProjectConfigFileName)))
            {
                result.AddRange(CollectProjectSubdirs(path, false));
            }
        }

        return result;
    }

    static void Prepare(BuildNode node, Env env)
    {
        using var writer = File.CreateText($"{node.BuildPath}/CMakeLists.txt");
        string[] srcExts = {"*.cpp", "*.c"};
        var srcPaths = CollectProjectSubdirs(node.SourcePath, true);
        List<string> srcPathExts = new List<string>();
        foreach (var srcPath in srcPaths)
        {
            var relSrcPath = Path.GetRelativePath(node.SourcePath, srcPath);
            foreach (var srcExt in srcExts)
            {
                srcPathExts.Add(Path.Combine(relSrcPath, srcExt));
            }
        }

        var srcSep = "\n    _/";
        var context = new CMakeTemplate.Context
        {
            SrcRoot = env.SourceRoot,
            BuildRoot = env.BuildRoot,
            SrcPaths = srcSep + string.Join(srcSep, srcPathExts) + "\n",
            ProjectName = node.Project.name
        };

        /// todo: expand to dll
        if (node.Project.type == ProjectSpec.Type.exe)
        {
            var libNames = new List<string>();
            var libPaths = new List<string>();
            var packageNames = new List<string>();
            var packagePaths = new List<string>();
            foreach (var depNode in node.CollectTransitiveLibraryDependers())
            {
                if (depNode.Project.type == ProjectSpec.Type.ext)
                {
                    packageNames.Add(depNode.Alias);
                    packagePaths.Add(depNode.BuildPath);
                } else
                {
                    libNames.Add(depNode.Name);
                    libPaths.Add(depNode.BuildPath);
                }
            }

            context.DepLibNames = string.Join(" ", libNames);
            context.DepLibPaths = string.Join(" ", libPaths);
            context.DepPackageNames = string.Join(" ", packageNames);
            context.DepPackagePaths = string.Join(" ", packagePaths);
        }

        switch (node.Project.type)
        {
            case ProjectSpec.Type.exe:
                writer.WriteLine(CMakeTemplate.ComposeExe(context));
                break;
            case ProjectSpec.Type.lib:
                writer.WriteLine(CMakeTemplate.ComposeLib(context));
                break;
            default:
                throw new Exception($"Unknown type: {node.Project.type}");
        }
    }

    static void RunExternal(string cmd, string args, string wd, bool verbose)
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
            var rawCmd = cmd + " " + string.Join(' ', args);
            if (process == null)
            {
                throw new Exception($"Bad cmd: {rawCmd}, working dir: {wd}");
            }

            process.WaitForExit();
            if (verbose)
            {
                string output = process.StandardOutput.ReadToEnd();
                Console.WriteLine($"Running '{rawCmd}' from '{wd}'");
                Console.WriteLine(output);
            }

            if (process.ExitCode != 0) {
                var error = process.StandardError.ReadToEnd();
                Console.WriteLine(error);
                throw new Exception($"Failed to run '{rawCmd}' from dir {wd}");
            }
        }
    }
}

