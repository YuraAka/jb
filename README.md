# Just Build system
## Aims
- Simple use: go to any folder with jb.yaml and invoke `jb build` to build target
- C++ & C# support

## Requirements
- cmake
- dotnet

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
