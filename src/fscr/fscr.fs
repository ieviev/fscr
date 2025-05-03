module fscr

open System.IO
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Text
open FSharp.Compiler.Syntax
open FSharp.Compiler.Symbols
open System.Reflection

module Msbuild =
    open Ionide.ProjInfo

    let tools_path(fsproj_path: string) =
        let dirname = Path.GetDirectoryName(fsproj_path)
        let dir = DirectoryInfo(dirname)
        Init.init dir None

    let graph_loader(tools_path) : IWorkspaceLoader =
        WorkspaceLoaderViaProjectGraph.Create(tools_path, [])

[<AutoOpen>]
module Helpers =
    open System
    open System.Threading.Tasks

    let dotnet_root =
        System.Environment.GetEnvironmentVariable("DOTNET_ROOT")
        |> Option.ofObj
        |> Option.defaultWith (fun _ -> failwith "set DOTNET_ROOT env variable")

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

    member val InMemoryStream: MemoryStream = new MemoryStream() with get, set
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
            | Some(FileMode.Open) ->
                // this seems like not required
                // this.InMemoryStream <- new MemoryStream(this.InMemoryStream.ToArray())
                // this.InMemoryStream
                MemoryStream.Null
            | _ -> failwith $"unexpected mode: {mode}"


let try_compile_script
    (opts: Options)
    (fsxPath: string)
    (sourceText: ISourceText)
    (checker: FSharpChecker)
    =
    let defaultFS = FSharp.Compiler.IO.FileSystemAutoOpens.FileSystem
    let memoryFS = MemoryFileSystem()
    FSharp.Compiler.IO.FileSystemAutoOpens.FileSystem <- memoryFS
    let sourceText = sourceText

    task {
        let! proj_opts, _ =
            checker.GetProjectOptionsFromScript(
                fsxPath,
                sourceText,
                assumeDotNetFramework = false,
                useFsiAuxLib = true,
                useSdkRefs = true
            )


        let! compile_result, exit_code =
            checker.Compile(
                [|
                    "fsc.exe"
                    $"--target:{opts.target}"
                    "--debug-"
                    "--optimize-"
                    "--nowin32manifest"
                    yield! proj_opts.OtherOptions
                    $"--out:output.dll"
                    yield!
                        (proj_opts.ReferencedProjects |> Array.map (fun v -> v.OutputFile))
                    yield! proj_opts.SourceFiles
                |]
            )

        match exit_code with
        | None ->
            let assembly = memoryFS.InMemoryStream.ToArray()
            FSharp.Compiler.IO.FileSystemAutoOpens.FileSystem <- defaultFS
            return proj_opts, assembly
        | Some exn ->
            compile_result |> Array.iter (fun v -> stdout.WriteLine $"%A{v}")
            return raise exn
    }

let try_compile_proj
    (opts: Options)
    (proj_opts: Ionide.ProjInfo.Types.ProjectOptions)
    (checker: FSharpChecker)
    =
    let default_fs = FSharp.Compiler.IO.FileSystemAutoOpens.FileSystem
    let memory_fs = MemoryFileSystem()
    FSharp.Compiler.IO.FileSystemAutoOpens.FileSystem <- memory_fs

    task {


        let! compile_result, exit_code =
            checker.Compile(
                [|
                    "fsc.exe"
                    $"--target:{opts.target}"
                    // "--debug-"
                    "--optimize-"
                    "--nowin32manifest"
                    yield! proj_opts.OtherOptions
                    $"--out:output.dll"
                    // todo: put other project refs here?
                    yield! proj_opts.SourceFiles
                |]
            )

        match exit_code with
        | None ->
            let assembly = memory_fs.InMemoryStream.ToArray()
            FSharp.Compiler.IO.FileSystemAutoOpens.FileSystem <- default_fs

            return {|
                assembly = assembly
                projOpts = proj_opts
            |}
        | Some exn ->
            compile_result |> Array.iter (fun v -> stdout.WriteLine $"%A{v}")
            return raise exn
    }

let copy_dlls_to_output
    (dest_folder: string)
    (other_options: string seq)
    (referenced_projects: string seq)
    : System.Threading.Tasks.Task =
    task {
        for opt in other_options do
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

        // replace obj/ references with real references
        for rp in referenced_projects do
            let nameonly = Path.GetFileName(rp)
            let dest_path = Path.Combine(dest_folder, nameonly)

            if File.Exists(dest_path) then
                File.Delete(dest_path)

            File.CreateSymbolicLink(dest_path, rp) |> ignore
    }


[<EntryPoint>]
let main argv =
    if Array.contains "--help" argv then
        failwith $"usage: fscr [-t library|exe] <script.fsx>"

    let opts = parse_options Options.Default argv
    let dest_path = "bin/"
    Directory.CreateDirectory(dest_path) |> ignore
    let watch = System.Diagnostics.Stopwatch.StartNew()

    let inline startup_msg() =
        stdout.WriteLine $"target={opts.target}"

    match opts.file_path with
    | Some path when path.EndsWith(".fsproj") ->
        let proj_full_path = Path.GetFullPath(path)
        let tools_path = Msbuild.tools_path path
        let loader = Msbuild.graph_loader tools_path
        let proj_option_list = loader.LoadProjects([ path ]) |> Seq.toArray
        let scriptname = Path.GetFileNameWithoutExtension(path)
        let runtimeconf_path = Path.Combine(dest_path, $"{scriptname}.runtimeconfig.json")
        startup_msg ()

        let project_file_names =
            proj_option_list |> Array.map (fun v -> v.ProjectFileName)

        // todo: this has all the referenced projects
        // but we're only compiling the current one
        let proj_opts =
            proj_option_list
            |> Array.tryFind (fun v ->
                Path.GetFullPath(v.ProjectFileName) = proj_full_path)
            |> Option.defaultWith (fun _ ->
                failwith
                    $"expected to find: {proj_full_path}, found: %A{project_file_names}")

        let referenced_projects =
            proj_option_list
            |> Array.choose (fun v ->
                match Path.GetFullPath(v.ProjectFileName) <> proj_full_path with
                | true -> Some v.TargetPath
                | _ -> None)

        let filter_options(projOtherOptions: string list) = [
            for p in projOtherOptions do
                if p.StartsWith("-o:") then
                    ()
                // if p.Contains("/obj/") then
                //     ()
                // else
                p
        ]

        let checker = FSharpChecker.Create(useTransparentCompiler = true)

        let compile_project() =
            task {
                do! stdout.WriteAsync ".."
                watch.Start()
                let! result = try_compile_proj opts proj_opts checker
                let prog = Path.Combine(dest_path, $"{scriptname}.dll")

                do!
                    System.Threading.Tasks.Task.WhenAll(
                        [|
                            if
                                opts.target = "exe" && not (File.Exists(runtimeconf_path))
                            then
                                File.WriteAllBytesAsync(runtimeconf_path, runtimeconfig)
                            copy_dlls_to_output
                                dest_path
                                (filter_options proj_opts.OtherOptions)
                                referenced_projects
                            File.WriteAllBytesAsync(prog, result.assembly)
                        |]
                    )

                watch.Stop()
                do! stdout.WriteLineAsync $" {watch.ElapsedMilliseconds}ms"
                watch.Reset()

                if opts.target = "exe" then
                    use ps = System.Diagnostics.Process.Start("dotnet", prog)
                    do! ps.WaitForExitAsync()
            }

        compile_project().Wait()
        let watcher = create_fs_watcher (Path.GetDirectoryName(path))
        watch_file watcher (fun _ -> compile_project ())

    | Some path when path.EndsWith(".fsx") ->

        let scriptname = Path.GetFileNameWithoutExtension(path)
        let runtimeconf_path = Path.Combine(dest_path, $"{scriptname}.runtimeconfig.json")
        startup_msg ()
        let checker = FSharpChecker.Create(useTransparentCompiler = true)

        let compile_script() =
            task {
                do! stdout.WriteAsync ".."
                watch.Start()

                let! str = File.ReadAllTextAsync(path)

                let! projOpts, assembly =
                    try_compile_script opts path (SourceText.ofString (str)) checker

                let prog = Path.Combine(dest_path, $"{scriptname}.dll")


                do!
                    System.Threading.Tasks.Task.WhenAll(
                        [|
                            if
                                opts.target = "exe" && not (File.Exists(runtimeconf_path))
                            then
                                File.WriteAllBytesAsync(runtimeconf_path, runtimeconfig)
                            copy_dlls_to_output dest_path (projOpts.OtherOptions) []
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
