# fscr

a wrapper around the F# compiler

```sh
usage: fscr [-t exe|library] <script.fsx / project.fsproj> (default: exe)
```

the output is created into `bin/` relative to workdir and executed after compiling, 
- with `-t library` only a dll is created

the reason to use this over `dotnet build` or `dotnet run` is: 
- that it takes ~300ms to recompile and run after changes (instead of several seconds!)
- all dll references are symlinked instead of copied
- the compilation is done with an in-memory filesystem
- the same compiler process is reused after first compilation
- also recompiles any referenced projects much faster

example:

https://github.com/user-attachments/assets/72f5da5a-b07d-4521-be45-fdc174772c19

### building: 

either run as a project 
```
dotnet run -c Release --project ../fscr/fscr.fsproj -- <path/to/myproject>.fsproj
```

or compile the project from source with Ready2Run, this drastically reduces initial startup time: 
- `dotnet publish src/fscr/ -p:PublishReadyToRun=true --ucr`
- `./artifacts/publish/fscr/release/fscr <path/to/myproject>.fsproj`





