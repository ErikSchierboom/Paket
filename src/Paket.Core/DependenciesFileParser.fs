﻿namespace Paket

module DependenciesFileParser =
    open System
    open System.IO
    open Requirements
    open ModuleResolver
    open Domain
    open PackageSources
    open Logging

    let private operators =
        VersionRange.BasicOperators
        @ (VersionRange.BasicOperators |> List.map (fun o -> VersionRange.StrategyOperators |> List.map (fun s -> string s + o)) |> List.concat)

    let (|NuGetStrategy|PaketStrategy|NoStrategy|) (text : string) =
        match text |> Seq.tryHead with
        | Some('!') -> NuGetStrategy
        | Some('@') -> PaketStrategy
        | _ -> NoStrategy

    let parseResolverStrategy (text : string) = 
        match text with
        | NuGetStrategy -> Some ResolverStrategy.Min
        | PaketStrategy -> Some ResolverStrategy.Max
        | NoStrategy -> None

    let twiddle(minimum:string) =
        let promote index (values:string array) =
            let parsed, number = Int32.TryParse values.[index]
            if parsed then values.[index] <- (number + 1).ToString()
            if values.Length > 1 then values.[values.Length - 1] <- "0"
            values

        let parts = minimum.Split '.'
        let penultimateItem = Math.Max(parts.Length - 2, 0)
        let promoted = parts |> promote penultimateItem
        String.Join(".", promoted)

    let parseVersionRequirement (text : string) : VersionRequirement =
        try
            let inline parsePrerelease (versions:SemVerInfo list) (texts : string list) = 
                match texts |> List.filter ((<>) "") with
                | [] -> 
                    versions
                    |> List.collect (fun version -> 
                        match version.PreRelease with 
                        | Some x -> [x.Name]
                        | _ -> [])
                    |> List.distinct
                    |> fun xs -> 
                        match xs with
                        | [] -> PreReleaseStatus.No
                        | _ -> PreReleaseStatus.Concrete xs
                | [x] when String.equalsIgnoreCase x "prerelease" -> PreReleaseStatus.All
                | _ -> PreReleaseStatus.Concrete texts

            if String.IsNullOrWhiteSpace text then VersionRequirement(VersionRange.AtLeast("0"),PreReleaseStatus.No) else

            match text.Split([|' '|],StringSplitOptions.RemoveEmptyEntries) |> Array.toList with
            |  ">=" :: v1 :: "<" :: v2 :: rest ->
                let v1 = SemVer.Parse v1
                let v2 = SemVer.Parse v2
                VersionRequirement(VersionRange.Range(VersionRangeBound.Including,v1,v2,VersionRangeBound.Excluding),parsePrerelease [v1; v2] rest)
            |  ">=" :: v1 :: "<=" :: v2 :: rest ->
                let v1 = SemVer.Parse v1
                let v2 = SemVer.Parse v2
                VersionRequirement(VersionRange.Range(VersionRangeBound.Including,v1,v2,VersionRangeBound.Including),parsePrerelease [v1; v2] rest)
            |  "~>" :: v1 :: ">=" :: v2 :: rest -> 
                let v1 = SemVer.Parse(twiddle v1)
                let v2 = SemVer.Parse v2
                VersionRequirement(VersionRange.Range(VersionRangeBound.Including,v2,v1,VersionRangeBound.Excluding),parsePrerelease [v1; v2] rest)
            |  "~>" :: v1 :: ">" :: v2 :: rest ->
                let v1 = SemVer.Parse(twiddle v1)
                let v2 = SemVer.Parse v2
                VersionRequirement(VersionRange.Range(VersionRangeBound.Excluding,v2,v1,VersionRangeBound.Excluding),parsePrerelease [v1; v2] rest)
            |  ">" :: v1 :: "<" :: v2 :: rest -> 
                let v1 = SemVer.Parse v1
                let v2 = SemVer.Parse v2
                VersionRequirement(VersionRange.Range(VersionRangeBound.Excluding,v1,v2,VersionRangeBound.Excluding),parsePrerelease [v1; v2] rest)
            |  ">" :: v1 :: "<=" :: v2 :: rest ->
                let v1 = SemVer.Parse v1
                let v2 = SemVer.Parse v2
                VersionRequirement(VersionRange.Range(VersionRangeBound.Excluding,v1,v2,VersionRangeBound.Including),parsePrerelease [v1; v2] rest)
            | _ -> 
                let splitVersion (text:string) =
                    match VersionRange.BasicOperators |> List.tryFind(text.StartsWith) with
                    | Some token -> token, text.Replace(token + " ", "").Split(' ') |> Array.toList
                    | None -> "=", text.Split(' ') |> Array.toList

            
                match splitVersion text with
                | "==", version :: rest -> 
                    let v = SemVer.Parse version
                    VersionRequirement(VersionRange.OverrideAll v,parsePrerelease [v] rest)
                | ">=", version :: rest -> 
                    let v = SemVer.Parse version
                    VersionRequirement(VersionRange.Minimum v,parsePrerelease [v] rest)
                | ">", version :: rest -> 
                    let v = SemVer.Parse version
                    VersionRequirement(VersionRange.GreaterThan v,parsePrerelease [v] rest)
                | "<", version :: rest -> 
                    let v = SemVer.Parse version
                    VersionRequirement(VersionRange.LessThan v,parsePrerelease [v] rest)
                | "<=", version :: rest -> 
                    let v = SemVer.Parse version
                    VersionRequirement(VersionRange.Maximum v,parsePrerelease [v] rest)
                | "~>", minimum :: rest -> 
                    let v1 = SemVer.Parse minimum
                    VersionRequirement(VersionRange.Between(minimum,twiddle minimum),parsePrerelease [v1] rest)
                | _, version :: rest -> 
                    let v = SemVer.Parse version
                    VersionRequirement(VersionRange.Specific v,parsePrerelease [v] rest)
                | _ -> failwithf "could not parse version range \"%s\"" text
        with
        | _ -> failwithf "could not parse version range \"%s\"" text

    let parseDependencyLine (line:string) =
        let rec parseDepLine start acc =
            if start >= line.Length then acc
            else
                match line.[start] with
                | ' ' -> parseDepLine (start+1) acc
                | '"' ->
                    match line.IndexOf('"', start+1) with
                    | -1  -> failwithf "Unclosed quote in line '%s'" line
                    | ind -> parseDepLine (ind+1) (line.Substring(start+1, ind-start-1)::acc)
                | _ ->
                    match line.IndexOf(' ', start+1) with
                    | -1  -> line.Substring(start)::acc
                    | ind -> parseDepLine (ind+1) (line.Substring(start, ind-start)::acc)

        parseDepLine 0 []
        |> List.rev
        |> List.toArray


    let private parseGitSource trimmed origin originTxt = 
        let parts = parseDependencyLine trimmed
        
        let getParts (projectSpec : string) = 
            match projectSpec.Split [| ':'; '/' |] with
            | [| owner; project |] -> owner, project, None
            | [| owner; project; commit |] -> owner, project, Some commit
            | _ -> failwithf "invalid %s specification:%s     %s" originTxt Environment.NewLine trimmed
        match parts with
        | [| _; projectSpec; fileSpec; authKey |] -> origin, getParts projectSpec, fileSpec, (Some authKey)
        | [| _; projectSpec; fileSpec |] -> origin, getParts projectSpec, fileSpec, None
        | [| _; projectSpec |] -> origin, getParts projectSpec, Constants.FullProjectSourceFileName, None
        | _ -> failwithf "invalid %s specification:%s     %s" originTxt Environment.NewLine trimmed


    let private parseHttpSource trimmed = 
        let parts = parseDependencyLine trimmed
        
        let getParts (projectSpec : string) fileSpec projectName authKey = 
            let projectSpec = projectSpec.TrimEnd('/')
            
            let projectSpec', commit =
                let start = 
                    match projectSpec.IndexOf("://") with
                    | -1 -> 8 // 8 = "https://".Length
                    | pos -> pos + 3
             
                match projectSpec.IndexOf('/', start) with 
                | -1 -> projectSpec, "/"
                | pos -> projectSpec.Substring(0, pos), projectSpec.Substring(pos)
            
            let splitted = projectSpec.TrimEnd('/').Split([| ':'; '/' |], StringSplitOptions.RemoveEmptyEntries)
            
            let fileName = 
                if String.IsNullOrEmpty fileSpec then
                    let name = Seq.last splitted
                    if String.IsNullOrEmpty <| Path.GetExtension(name) then name + ".fs"
                    else name
                else fileSpec
            
            let owner = 
                match projectSpec'.IndexOf("://") with
                | -1 -> projectSpec'
                | pos -> projectSpec'.Substring(pos + 3) |> removeInvalidChars
            
            HttpLink(projectSpec'), (owner, projectName, Some commit), fileName, authKey

        match parseDependencyLine trimmed with
        | [| spec; url |] -> getParts url "" "" None
        | [| spec; url; fileSpec |] -> getParts url fileSpec "" None
        | [| spec; url; fileSpec; authKey |] -> getParts url fileSpec "" (Some authKey)
        | _ -> failwithf "invalid http-reference specification:%s     %s" Environment.NewLine trimmed

    type private ParserOption =
    | ReferencesMode of bool
    | OmitContent of ContentCopySettings
    | FrameworkRestrictions of FrameworkRestrictions
    | AutodetectFrameworkRestrictions
    | ImportTargets of bool
    | CopyLocal of bool
    | CopyContentToOutputDir of CopyToOutputDirectorySettings
    | ReferenceCondition of string
    | Redirects of bool option
    | ResolverStrategyForTransitives of ResolverStrategy option
    | ResolverStrategyForDirectDependencies of ResolverStrategy option

    type RemoteParserOption =
    | PackageSource of PackageSource
    | Cache of Cache

    let private (|Remote|Package|Empty|ParserOptions|SourceFile|Git|Group|) (line:string) =
        let trimmed = line.Trim()

        let removeComment (text:string) =
            match text.IndexOf("//") with
            | -1 ->
                match text.IndexOf("#") with
                | -1 -> text
                | p -> 
                    let f = text.Substring(0,p).Trim()
                    printfn "%s" f
                    f
            | p -> 
                let f = text.Substring(0,p).Trim()
                printfn "%s" f
                f
            

        match trimmed with
        | _ when String.IsNullOrWhiteSpace line -> Empty(line)
        | String.StartsWith "source" _ as trimmed -> Remote(RemoteParserOption.PackageSource(PackageSource.Parse(trimmed)))
        | String.StartsWith "cache" _ as trimmed -> Remote(RemoteParserOption.Cache(Cache.Parse(trimmed)))
        | String.StartsWith "group" _ as trimmed -> Group(trimmed.Replace("group ",""))
        | String.StartsWith "nuget" trimmed -> 
            let parts = trimmed.Trim().Replace("\"", "").Split([|' '|],StringSplitOptions.RemoveEmptyEntries) |> Seq.toList

            let isVersion(text:string) = 
                match Int32.TryParse(text.[0].ToString()) with
                | true,_ -> true
                | _ -> false
           
            match parts with
            | name :: operator1 :: version1  :: operator2 :: version2 :: rest
                when List.exists ((=) operator1) operators && List.exists ((=) operator2) operators -> 
                Package(name,operator1 + " " + version1 + " " + operator2 + " " + version2, String.Join(" ",rest) |> removeComment)
            | name :: operator :: version  :: rest 
                when List.exists ((=) operator) operators ->
                Package(name,operator + " " + version, String.Join(" ",rest) |> removeComment)
            | name :: version :: rest when isVersion version -> 
                Package(name,version,String.Join(" ",rest) |> removeComment)
            | name :: rest -> Package(name,">= 0", String.Join(" ",rest) |> removeComment)
            | [name] -> Package(name,">= 0","")
            | _ -> failwithf "could not retrieve NuGet package from %s" trimmed
        | String.StartsWith "references" trimmed -> ParserOptions(ParserOption.ReferencesMode(trimmed.Replace(":","").Trim() = "strict"))
        | String.StartsWith "redirects" trimmed ->
            let setting =
                match trimmed.Replace(":","").Trim().ToLowerInvariant() with
                | "on" -> Some true
                | "off" -> Some false
                | _ -> None

            ParserOptions(ParserOption.Redirects(setting))
        | String.StartsWith "strategy" trimmed -> 
            let setting =
                match trimmed.Replace(":","").Trim().ToLowerInvariant() with
                | "max" -> Some ResolverStrategy.Max
                | "min" -> Some ResolverStrategy.Min
                | _ -> None

            ParserOptions(ParserOption.ResolverStrategyForTransitives(setting))
        | String.StartsWith "lowest_matching" trimmed -> 
            let setting =
                match trimmed.Replace(":","").Trim().ToLowerInvariant() with
                | "false" -> Some ResolverStrategy.Max
                | "true" -> Some ResolverStrategy.Min
                | _ -> None

            ParserOptions(ParserOption.ResolverStrategyForDirectDependencies(setting))
        | String.StartsWith "framework" trimmed -> 
            let text = trimmed.Replace(":", "").Trim()
            
            if text = "auto-detect" then 
                ParserOptions(ParserOption.AutodetectFrameworkRestrictions)
            else 
                let restrictions = Requirements.parseRestrictions text
                if String.IsNullOrWhiteSpace text |> not && List.isEmpty restrictions then 
                    failwithf "Could not parse framework restriction \"%s\"" text

                let options = ParserOption.FrameworkRestrictions(FrameworkRestrictionList restrictions)
                ParserOptions options

        | String.StartsWith "content" trimmed -> 
            let setting =
                match trimmed.Replace(":","").Trim().ToLowerInvariant() with
                | "none" -> ContentCopySettings.Omit
                | "once" -> ContentCopySettings.OmitIfExisting
                | _ -> ContentCopySettings.Overwrite

            ParserOptions(ParserOption.OmitContent(setting))
        | String.StartsWith "import_targets" trimmed -> ParserOptions(ParserOption.ImportTargets(trimmed.Replace(":","").Trim() = "true"))
        | String.StartsWith "copy_local" trimmed -> ParserOptions(ParserOption.CopyLocal(trimmed.Replace(":","").Trim() = "true"))
        | String.StartsWith "copy_content_to_output_dir" trimmed -> 
            let setting =
                match trimmed.Replace(":","").Trim().ToLowerInvariant() with
                | "always" -> CopyToOutputDirectorySettings.Always
                | "never" -> CopyToOutputDirectorySettings.Never
                | "preserve_newest" -> CopyToOutputDirectorySettings.PreserveNewest
                | x -> failwithf "Unknown copy_content_to_output_dir settings: %A" x
                        
            ParserOptions(ParserOption.CopyContentToOutputDir(setting))
        | String.StartsWith "condition" trimmed -> ParserOptions(ParserOption.ReferenceCondition(trimmed.Replace(":","").Trim().ToUpper()))
        | String.StartsWith "gist" _ as trimmed ->
            SourceFile(parseGitSource trimmed Origin.GistLink "gist")
        | String.StartsWith "github" _ as trimmed  ->
            SourceFile(parseGitSource trimmed Origin.GitHubLink "github")
        | String.StartsWith "git" _ as trimmed  ->
            Git(trimmed.Substring(4))
        | String.StartsWith "file:" _ as trimmed  ->
            Git(trimmed)
        | String.StartsWith "http" _ as trimmed  ->
            SourceFile(parseHttpSource trimmed)
        | String.StartsWith "//" _ -> Empty(line)
        | String.StartsWith "#" _ -> Empty(line)
        | _ -> failwithf "Unrecognized token: %s" line
    
    let parsePackage(sources,parent,name,version,rest:string) =
        let prereleases,optionsText =
            if rest.Contains ":" then
                // boah that's reaaaally ugly, but keeps backwards compat
                let pos = rest.IndexOf ':'
                let s = rest.Substring(0,pos).TrimEnd()
                let pos' = s.LastIndexOf(' ')
                let prereleases = if pos' > 0 then s.Substring(0,pos') else ""
                let s' = if prereleases <> "" then rest.Replace(prereleases,"") else rest
                prereleases,s'
            else
                rest,""

        if operators |> Seq.exists prereleases.Contains || prereleases.Contains("!") then
            failwithf "Invalid prerelease version %s" prereleases

        let packageName = PackageName name

        let vr = (version + " " + prereleases).Trim(VersionRange.StrategyOperators |> Array.ofList)
        let versionRequirement = parseVersionRequirement vr

        { Name = packageName
          ResolverStrategyForTransitives = 
            if optionsText.Contains "strategy" then 
                let kvPairs = parseKeyValuePairs optionsText
                match kvPairs.TryGetValue "strategy" with
                | true, "max" -> Some ResolverStrategy.Max 
                | true, "min" -> Some ResolverStrategy.Min
                | _ -> parseResolverStrategy version
            else parseResolverStrategy version 
          ResolverStrategyForDirectDependencies = 
            if optionsText.Contains "lowest_matching" then 
                let kvPairs = parseKeyValuePairs optionsText
                match kvPairs.TryGetValue "lowest_matching" with
                | true, "false" -> Some ResolverStrategy.Max 
                | true, "true" -> Some ResolverStrategy.Min
                | _ -> None
            else None 
          Parent = parent
          Graph = []
          Sources = sources
          Settings = InstallSettings.Parse(optionsText).AdjustWithSpecialCases packageName
          VersionRequirement = versionRequirement } 

    let parsePackageLine(sources,parent,line:string) =
        match line with 
        | Package(name,version,rest) -> parsePackage(sources,parent,name,version,rest)
        | _ -> failwithf "Not a package line: %s" line

    let private parseOptions (current  : DependenciesGroup) options =
        match options with 
        | ReferencesMode mode -> { current.Options with Strict = mode } 
        | Redirects mode -> { current.Options with Redirects = mode }
        | ResolverStrategyForTransitives strategy -> { current.Options with ResolverStrategyForTransitives = strategy }
        | ResolverStrategyForDirectDependencies strategy -> { current.Options with ResolverStrategyForDirectDependencies = strategy }
        | CopyLocal mode -> { current.Options with Settings = { current.Options.Settings with CopyLocal = Some mode } }
        | CopyContentToOutputDir mode -> { current.Options with Settings = { current.Options.Settings with CopyContentToOutputDirectory = Some mode } }
        | ImportTargets mode -> { current.Options with Settings = { current.Options.Settings with ImportTargets = Some mode } }
        | FrameworkRestrictions r -> { current.Options with Settings = { current.Options.Settings with FrameworkRestrictions = r } }
        | AutodetectFrameworkRestrictions ->
            { current.Options with Settings = { current.Options.Settings with FrameworkRestrictions = AutoDetectFramework } }
        | OmitContent omit -> { current.Options with Settings = { current.Options.Settings with OmitContent = Some omit } }
        | ReferenceCondition condition -> { current.Options with Settings = { current.Options.Settings with ReferenceCondition = Some condition } }

    let private parseLine fileName checkDuplicates (lineNo, state) line =
        match state with
        | current::other ->
            let lineNo = lineNo + 1
            try
                match line with
                | Group(newGroupName) -> lineNo, DependenciesGroup.New(GroupName newGroupName)::current::other
                | Empty(_) -> lineNo, current::other
                | Remote(RemoteParserOption.PackageSource newSource) -> lineNo, { current with Sources = current.Sources @ [newSource] |> List.distinct }::other
                | Remote(RemoteParserOption.Cache newCache) -> 
                    let caches = current.Caches @ [newCache] |> List.distinct
                    let sources = current.Sources @ [LocalNuGet(newCache.Location,Some newCache)] |> List.distinct
                    lineNo, { current with Caches = caches; Sources = sources }::other
                | ParserOptions(options) ->
                    lineNo,{ current with Options = parseOptions current options} ::other
                | Package(name,version,rest) ->
                    let package = parsePackage(current.Sources,DependenciesFile fileName,name,version,rest) 
                    if checkDuplicates && current.Packages |> List.exists (fun p -> p.Name = package.Name) then
                        traceWarnfn "Package %O is defined more than once in group %O of %s" package.Name current.Name fileName
                    
                    lineNo, { current with Packages = current.Packages  @ [package] }::other
                | SourceFile(origin, (owner,project, vr), path, authKey) ->
                    let remoteFile : UnresolvedSource = 
                        { Owner = owner
                          Project = project
                          Version = 
                            match vr with
                            | None -> VersionRestriction.NoVersionRestriction
                            | Some x -> VersionRestriction.Concrete x
                          Name = path
                          Origin = origin
                          Command = None
                          OperatingSystemRestriction = None
                          PackagePath = None
                          AuthKey = authKey }
                    lineNo, { current with RemoteFiles = current.RemoteFiles @ [remoteFile] }::other
                | Git(gitConfig) ->
                    let owner,vr,project,origin,buildCommand,operatingSystemRestriction,packagePath = Git.Handling.extractUrlParts gitConfig
                    let remoteFile : UnresolvedSource = 
                        { Owner = owner
                          Project = project
                          Version = 
                            match vr with
                            | None -> VersionRestriction.NoVersionRestriction
                            | Some x -> 
                                try 
                                    let vr = parseVersionRequirement x
                                    VersionRestriction.VersionRequirement vr
                                with 
                                | _ -> VersionRestriction.Concrete x
                          Command = buildCommand
                          OperatingSystemRestriction = operatingSystemRestriction
                          PackagePath = packagePath
                          Name = ""
                          Origin = GitLink origin
                          AuthKey = None }
                    let sources = 
                        match packagePath with
                        | None -> current.Sources
                        | Some path -> 
                            let root = ""
                            let fullPath = remoteFile.ComputeFilePath(root,current.Name,path)
                            let relative = (createRelativePath root fullPath).Replace("\\","/")
                            LocalNuGet(relative,None) :: current.Sources |> List.distinct 
                    lineNo, { current with RemoteFiles = current.RemoteFiles @ [remoteFile]; Sources = sources }::other
            with
            | exn -> failwithf "Error in paket.dependencies line %d%s  %s" lineNo Environment.NewLine exn.Message
        | [] -> failwithf "Error in paket.dependencies line %d" lineNo

    let parseDependenciesFile fileName checkDuplicates lines =
        let groups = 
            lines
            |> Array.fold (parseLine fileName checkDuplicates) (0, [DependenciesGroup.New Constants.MainDependencyGroup])
            |> snd
            |> List.rev
            |> List.fold (fun m g ->
                match Map.tryFind g.Name m with
                | Some group -> Map.add g.Name (g.CombineWith group) m
                | None -> Map.add g.Name g m) Map.empty

        fileName, groups, lines
    
    let parseVersionString (version : string) = 
        { VersionRequirement = parseVersionRequirement (version.Trim(VersionRange.StrategyOperators |> Array.ofList))
          ResolverStrategy = parseResolverStrategy version }



