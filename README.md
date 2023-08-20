# Just Build system
## Aims
- Simple use: go to any folder with jb.yaml and invoke `jb build` to build target
- C++ & C# support

## Format
jb.yaml has following format:

```
name: <name-of-target>
type: exe | lib
deps:
  - path-to-target-from-repo-root-1
  - path-to-target-from-repo-root-2
```
