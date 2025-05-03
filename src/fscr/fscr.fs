module fscr

open System.IO
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Text
open FSharp.Compiler.Syntax
open FSharp.Compiler.Symbols
open System.Reflection

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

    let watch_file (fsxPath: string) (hook: unit -> Task<unit>) =
        let dir = Path.GetDirectoryName(fsxPath)
        let filename = Path.GetFileName(fsxPath)

        use watcher =
            new FileSystemWatcher(
                dir,
                filename,
                EnableRaisingEvents = true,
                IncludeSubdirectories = true
            )

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
    member val InMemoryStream = new MemoryStream()
    override _.CopyShim(src, dest, overwrite) = base.CopyShim(src, dest, overwrite)
    override this.OpenFileForWriteShim(_, _, _, _) = this.InMemoryStream


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
        // |> Seq.where (fun v ->
        //     not (v.EndsWith(".fsproj.fsx")) && not (v.StartsWith(nuget_cache_path)))
        // |> Seq.toArray

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

let copy_dlls_to_output
    (dest_folder: string)
    (po: FSharpProjectOptions)
    : System.Threading.Tasks.Task =
    task {
        for opt in po.OtherOptions do
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

    match opts.file_path with
    | None -> failwith "expecting .fsx script"
    | Some fp ->
        let scriptname = Path.GetFileNameWithoutExtension(fp)
        let runtimeconf_path = Path.Combine(dest_path, $"{scriptname}.runtimeconfig.json")
        stdout.WriteLine $"target={opts.target}, waiting for changes.."

        watch_file fp (fun _ ->
            task {
                do! stdout.WriteAsync ".."

                watch.Start()

                let! str = File.ReadAllTextAsync(fp)

                let! projOpts, assembly =
                    tryCompileScriptInMemory opts fp (SourceText.ofString (str))

                let prog = Path.Combine(dest_path, $"{scriptname}.dll")


                do!
                    System.Threading.Tasks.Task.WhenAll(
                        [|
                            if
                                opts.target = "exe"
                                && not (File.Exists(runtimeconf_path))
                            then
                                File.WriteAllBytesAsync(runtimeconf_path, runtimeconfig)
                            copy_dlls_to_output dest_path (projOpts)
                            File.WriteAllBytesAsync(prog, assembly)
                        |]
                    )

                watch.Stop()
                do! stdout.WriteLineAsync $" {watch.ElapsedMilliseconds}ms"
                watch.Reset()

                if opts.target = "exe" then
                    use ps = System.Diagnostics.Process.Start("dotnet", prog)
                    do! ps.WaitForExitAsync()
            })

    0
