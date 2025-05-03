# fscr

a wrapper around the F# compiler

```sh
usage: fscr [-t exe|library] <script.fsx / project.fsproj> (default: exe)
```

the output is created into `bin/` and executed after compiling, 
- with `-t library` a dll is created and not ran

the reason to use this over `dotnet build` or `dotnet run` is: 
- that it takes 300ms to recompile and run after changes (instead of several seconds!)
- all dll references are symlinked instead of copied
- the compilation is done with an in-memory filesystem
- the same compiler process is reused after first compilation

example: running unit tests in a project

https://github.com/user-attachments/assets/ea93a3cc-12a2-46f0-9170-6ccef7299902

example: running script

https://github.com/user-attachments/assets/a84996e4-9867-424f-b283-b9d3c5d528a2

### building: 

compile the project from source with Ready2Run, this drastically reduces startup time: 
- `cd src/fscr`
- `dotnet publish -p:PublishReadyToRun=true --ucr`





