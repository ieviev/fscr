module fscr

open System.IO
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Text
open FSharp.Compiler.Syntax
open FSharp.Compiler.Symbols
open System.Reflection

module Msbuild =
    open Ionide.ProjInfo

    let toolsPath(fsproj_path: string) =
        let dirname = Path.GetDirectoryName(fsproj_path)
        let dir = DirectoryInfo(dirname)
        Init.init dir None

    let graphLoader(toolsPath) : IWorkspaceLoader =
        WorkspaceLoaderViaProjectGraph.Create(toolsPath, [])

    let subscribeNotifs(loader: IWorkspaceLoader) : System.IDisposable =
        loader.Notifications.Subscribe(fun msg ->
            // printfn "%A" msg
            ())


// let createProjOptions =
//     ()
// let asd = FCS.mapManyOptions projectOptions


[<AutoOpen>]
module Helpers =
    open System
    open System.Threading.Tasks

    let dotnet_root =
        System.Environment.GetEnvironmentVariable("DOTNET_ROOT")
        |> Option.ofObj
        |> Option.defaultWith (fun v -> failwith "set DOTNET_ROOT env variable")

    let dotnet_packs_ref = "-r:" + Path.Combine(dotnet_root, "packs")

    let runtimeconfig =
        """{ "runtimeOptions": { "tfm": "net9.0", "framework": { "name": "Microsoft.NETCore.App", "version": "9.0.0" }, "configProperties": { "System.Runtime.Serialization.EnableUnsafeBinaryFormatterSerialization": false } } }"""
        |> System.Text.Encoding.UTF8.GetBytes

    let nuget_cache_path =
        let userFolder =
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile)

        Path.Combine(userFolder, ".packagemanagement", "nuget")

    let time_between_s = 0.2

    let create_fs_watcher(directory: string) =
        new FileSystemWatcher(
            directory,
            "*.fs",
            EnableRaisingEvents = true,
            IncludeSubdirectories = true
        )

    let create_fsx_watcher(fsxPath: string) =
        let dir = Path.GetDirectoryName(fsxPath)
        let filename = Path.GetFileName(fsxPath)

        new FileSystemWatcher(
            dir,
            filename,
            EnableRaisingEvents = true,
            IncludeSubdirectories = true
        )

    let watch_file (watcher: FileSystemWatcher) (hook: unit -> Task<unit>) =
        let mutable nextproc = DateTimeOffset.Now

        task {
            while true do
                let _ = watcher.WaitForChanged(WatcherChangeTypes.Changed)

                match DateTimeOffset.Now > nextproc with
                | false -> ()
                | true ->
                    try
                        do! hook ()
                    with e ->
                        stderr.WriteLine e

                    nextproc <- (nextproc.AddSeconds(time_between_s))
        }
        |> (fun v -> v.GetAwaiter().GetResult())


    type Options = {
        mutable file_path: string option
        mutable target: string
    } with

        static member Default: Options = { file_path = None; target = "exe" }

    let inline next(e: byref<System.Span.Enumerator<string>>) =
        if not (e.MoveNext()) then
            failwith "<expected arg>"

        e.Current

    let rec parse_options (acc: Options) (argv: string[]) =
        let mutable e = argv.AsSpan().GetEnumerator()

        while e.MoveNext() do
            match e.Current with
            | "-t" -> acc.target <- next (&e)
            | s -> acc.file_path <- Some s

        acc


type MemoryFileSystem() =
    inherit FSharp.Compiler.IO.DefaultFileSystem()

    member val InMemoryStream: MemoryStream = null with get, set
    member val PdbStream = new MemoryStream()

    override this.OpenFileForWriteShim(filePath, mode, _, _) =
        // stdout.WriteLine $"open_file: {filePath}, {mode}"
        if filePath.EndsWith(".pdb") then
            this.PdbStream
        else
            match mode with
            | Some(FileMode.Create) ->
                this.InMemoryStream <- new MemoryStream()
                this.InMemoryStream
            | Some(FileMode.Open) -> new MemoryStream()
            | _ -> failwith $"unexpected mode: {mode}"


let tryCompileScriptInMemory (opts: Options) (fsxPath: string) (sourceText: ISourceText) =
    let defaultFS = FSharp.Compiler.IO.FileSystemAutoOpens.FileSystem
    let memoryFS = MemoryFileSystem()
    FSharp.Compiler.IO.FileSystemAutoOpens.FileSystem <- memoryFS
    let checker = FSharpChecker.Create()
    let sourceText = sourceText

    task {
        let! projOpts, _ =
            checker.GetProjectOptionsFromScript(
                fsxPath,
                sourceText,
                assumeDotNetFramework = false,
                useFsiAuxLib = true,
                useSdkRefs = true
            )


        let filteredSourceFiles = projOpts.SourceFiles

        let! compileResult, exitCode =
            checker.Compile(
                [|
                    "filename.fsx"
                    $"--target:{opts.target}"
                    "--debug-"
                    "--optimize-"
                    "--nowin32manifest"
                    yield! projOpts.OtherOptions
                    $"--out:output.dll"
                    yield!
                        (projOpts.ReferencedProjects |> Array.map (fun v -> v.OutputFile))
                    yield! filteredSourceFiles
                |]
            )

        match exitCode with
        | None ->
            let assembly = memoryFS.InMemoryStream.ToArray()
            FSharp.Compiler.IO.FileSystemAutoOpens.FileSystem <- defaultFS
            return projOpts, assembly
        | Some exn ->
            compileResult |> Array.iter (fun v -> stdout.WriteLine $"%A{v}")
            return raise exn
    }

let tryCompileProjectInMemory
    (opts: Options)
    (projOpts: Ionide.ProjInfo.Types.ProjectOptions)
    =
    let defaultFS = FSharp.Compiler.IO.FileSystemAutoOpens.FileSystem
    let memoryFS = MemoryFileSystem()
    FSharp.Compiler.IO.FileSystemAutoOpens.FileSystem <- memoryFS
    let checker = FSharpChecker.Create()

    task {
        let filteredSourceFiles = projOpts.SourceFiles

        let! compileResult, exitCode =
            checker.Compile(
                [|
                    "filename.fsx"
                    $"--target:{opts.target}"
                    "--debug-"
                    "--optimize-"
                    "--nowin32manifest"
                    yield! projOpts.OtherOptions
                    $"--out:output.dll"
                    yield!
                        (projOpts.ReferencedProjects
                         |> Seq.map (fun v -> v.ProjectFileName))
                    yield! filteredSourceFiles
                |]
            )

        match exitCode with
        | None ->
            let assembly = memoryFS.InMemoryStream.ToArray()
            FSharp.Compiler.IO.FileSystemAutoOpens.FileSystem <- defaultFS

            return {|
                assembly = assembly
                projOpts = projOpts
            |}
        | Some exn ->
            compileResult |> Array.iter (fun v -> stdout.WriteLine $"%A{v}")
            return raise exn
    }

let copy_dlls_to_output
    (dest_folder: string)
    (otherOptions: string seq)
    : System.Threading.Tasks.Task =
    task {
        for opt in otherOptions do
            if not (opt.StartsWith("-r:")) then
                ()
            else if (opt.StartsWith(dotnet_packs_ref)) then
                ()
            else

            let assembly_path = opt[3..]
            let nameonly = Path.GetFileName(assembly_path)
            let dest_path = Path.Combine(dest_folder, nameonly)

            if File.Exists(dest_path) then
                File.Delete(dest_path)

            File.CreateSymbolicLink(dest_path, assembly_path) |> ignore
    }


[<EntryPoint>]
let main argv =
    if Array.contains "--help" argv then
        failwith $"usage: fscr [-t library|exe] <script.fsx>"

    let opts = parse_options Options.Default argv
    let dest_path = "bin/"
    Directory.CreateDirectory(dest_path) |> ignore
    let watch = System.Diagnostics.Stopwatch.StartNew()

    let startup_msg() =
        stdout.WriteLine $"target={opts.target}"

    match opts.file_path with
    | Some path when path.EndsWith(".fsproj") ->
        let toolsPath = Msbuild.toolsPath path
        let loader = Msbuild.graphLoader toolsPath
        let projectOptionList = loader.LoadProjects([ path ]) |> Seq.toArray
        let scriptname = Path.GetFileNameWithoutExtension(path)
        let runtimeconf_path = Path.Combine(dest_path, $"{scriptname}.runtimeconfig.json")
        startup_msg ()

        if projectOptionList.Length <> 1 then
            let names = projectOptionList |> Array.map (fun v -> v.ProjectFileName)
            stdout.WriteLine $"expected 1 project, got %A{names}"

        let projOpts = projectOptionList[0]

        let compile_project() =
            task {
                let! result = tryCompileProjectInMemory opts projOpts
                let prog = Path.Combine(dest_path, $"{scriptname}.dll")

                do!
                    System.Threading.Tasks.Task.WhenAll(
                        [|
                            if
                                opts.target = "exe" && not (File.Exists(runtimeconf_path))
                            then
                                File.WriteAllBytesAsync(runtimeconf_path, runtimeconfig)
                            copy_dlls_to_output dest_path (projOpts.OtherOptions)
                            File.WriteAllBytesAsync(prog, result.assembly)
                        |]
                    )

                watch.Stop()
                do! stdout.WriteLineAsync $" {watch.ElapsedMilliseconds}ms"
                watch.Reset()

                if opts.target = "exe" then
                    use ps = System.Diagnostics.Process.Start("dotnet", prog)
                    do! ps.WaitForExitAsync()

                ()
            }

        compile_project().Wait()
        let watcher = create_fs_watcher (Path.GetDirectoryName(path))
        watch_file watcher (fun _ -> compile_project ())

    | Some path when path.EndsWith(".fsx") ->

        let scriptname = Path.GetFileNameWithoutExtension(path)
        let runtimeconf_path = Path.Combine(dest_path, $"{scriptname}.runtimeconfig.json")
        startup_msg ()

        let compile_script() =
            task {
                do! stdout.WriteAsync ".."

                watch.Start()

                let! str = File.ReadAllTextAsync(path)

                let! projOpts, assembly =
                    tryCompileScriptInMemory opts path (SourceText.ofString (str))

                let prog = Path.Combine(dest_path, $"{scriptname}.dll")


                do!
                    System.Threading.Tasks.Task.WhenAll(
                        [|
                            if
                                opts.target = "exe" && not (File.Exists(runtimeconf_path))
                            then
                                File.WriteAllBytesAsync(runtimeconf_path, runtimeconfig)
                            copy_dlls_to_output dest_path (projOpts.OtherOptions)
                            File.WriteAllBytesAsync(prog, assembly)
                        |]
                    )

                watch.Stop()
                do! stdout.WriteLineAsync $" {watch.ElapsedMilliseconds}ms"
                watch.Reset()

                if opts.target = "exe" then
                    use ps = System.Diagnostics.Process.Start("dotnet", prog)
                    do! ps.WaitForExitAsync()
            }

        compile_script().Wait()
        let watcher = create_fsx_watcher (path)
        watch_file watcher (fun _ -> compile_script ())
    | _ -> failwith "expecting .fsx or .fsproj"

    0
