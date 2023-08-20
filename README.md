# Just Build system
## Features
- Simple use: go to any folder in your repository and invoke `jb build` to build target
- No temporary files in repository -- only one symlink of built target in a project folder
- Can be run from any directory, even if it does not contain project file jb.yaml
- Simple minimalistic project file syntax (for static C++ libraries it's sufficient to empy file)
- C++

## Repository constraints
- Any folder contains files from one project only
- No symlinks allowed
- Repository root folder contains empy .jb.root file

## Requirements
- cmake
- dotnet

## Supported OS
- MacOS (tested)
- Windows, Linux (untested)

## How to use
1. Build jb-tool:
```
$ dotnet build
```

2. Build exe2 sample using jb-tool
```
$ cd exe2
$ jb b
```

3. Run built sample
```
$ ./exe2
```

4. Make your own projects...

## Format
jb.yaml has following format:

```
name: <name-of-target>
type: exe | lib
deps:
  - path-to-target-from-repo-root-1
  - path-to-target-from-repo-root-2
```
