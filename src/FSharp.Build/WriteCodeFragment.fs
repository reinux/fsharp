// Copyright (c) Microsoft Corporation.  All Rights Reserved.  See License.txt in the project root for license information.

namespace FSharp.Build

open System
open System.Collections
open System.IO
open System.Text
open Microsoft.Build.Framework
open Microsoft.Build.Utilities

[<Struct>]
type EscapedValue = { Escaped: string; Raw: string }

type WriteCodeFragment() as this =
    inherit Task()
    let mutable _outputDirectory: ITaskItem MaybeNull = null
    let mutable _outputFile: ITaskItem MaybeNull = null
    let mutable _language: string = ""
    let mutable _assemblyAttributes: ITaskItem[] = [||]

    let failTask fmt =
        Printf.ksprintf
            (fun msg ->
                this.Log.LogError msg
                raise TaskFailed)
            fmt

    static let escapeString (str: string) =
        let sb =
            str.ToCharArray()
            |> Seq.fold
                (fun (sb: StringBuilder) (c: char) ->
                    match c with
                    | '\n'
                    | '\u2028'
                    | '\u2028' -> sb.Append("\\n")
                    | '\r' -> sb.Append("\\r")
                    | '\t' -> sb.Append("\\t")
                    | '\'' -> sb.Append("\\'")
                    | '\\' -> sb.Append("\\\\")
                    | '"' -> sb.Append("\\\"")
                    | '\u0000' -> sb.Append("\\0")
                    | _ -> sb.Append(c))
                (StringBuilder().Append("\""))

        {
            Escaped = sb.Append("\"").ToString()
            Raw = str
        }

    member _.GenerateAttribute(item: ITaskItem, language: string) =
        let attributeName = item.ItemSpec

        let args =
            // mimicking the behavior from https://github.com/Microsoft/msbuild/blob/70ce7e9ccb891b63f0859f1f7f0b955693ed3742/src/Tasks/WriteCodeFragment.cs#L355-L415
            // Split parameters into unnamed positional (e.g., key is "_Parameter1", etc.) and proper named parameters.
            let customMetadata = item.CloneCustomMetadata()

            let parameterPairs =
                // normalize everything to strings
                seq { for entry in customMetadata -> entry :?> DictionaryEntry }
                |> Seq.toList
                |> List.map (fun entry ->
                    let key = entry.Key :?> string

                    let value =
                        match entry.Value with
                        | null -> { Escaped = "null"; Raw = "null" }
                        | :? string as strValue -> escapeString strValue
                        | value ->
                            let strValue = value.ToString()
                            { Escaped = strValue; Raw = strValue }

                    (key, value))

            let orderedParameters, namedParameters =
                parameterPairs |> List.partition (fun (key, _) -> key.StartsWith("_Parameter"))

            let orderedParametersWithIndex =
                orderedParameters
                |> List.map (fun (key, value) ->
                    let indexString = key.Substring("_Parameter".Length)

                    match Int32.TryParse indexString with
                    | (true, index) -> (index, value)
                    | (false, _) -> failTask "Unable to parse '%s' as an index" indexString)
                |> List.sortBy fst
            // assign ordered parameters to array
            let orderedParametersArray =
                if List.isEmpty orderedParametersWithIndex then
                    [||]
                else
                    Array.create (List.last orderedParametersWithIndex |> fst) "null"

            List.iter (fun (index, value) -> orderedParametersArray.[index - 1] <- value.Escaped) orderedParametersWithIndex
            // construct ordered parameter lists
            let combinedOrderedParameters = String.Join(", ", orderedParametersArray)

            let combinedNamedParameters =
                // Define "_IsLiteral" suffix to match MSBuild behavior
                let isLiteralSuffix = "_IsLiteral"

                // Process named parameters to handle IsLiteral suffix
                let processedNamedParameters =
                    // First identify all parameters with _IsLiteral suffix
                    let isLiteralParams =
                        namedParameters
                        |> List.choose (fun (key, value) ->
                            if key.EndsWith(isLiteralSuffix) && (value.Raw = "true" || value.Raw = "True") then
                                // Extract the base parameter name by removing the suffix
                                Some(key.Substring(0, key.Length - isLiteralSuffix.Length))
                            else
                                None)
                        |> Set.ofList

                    // Process all parameters, handling literals appropriately
                    namedParameters
                    |> List.choose (fun (key, value) ->
                        // Skip _IsLiteral metadata entries
                        if key.EndsWith(isLiteralSuffix) then
                            None
                        else
                            // Check if this parameter should be treated as a literal
                            let isLiteral = Set.contains key isLiteralParams

                            if isLiteral then
                                // For literals, use the raw value
                                Some(key, value.Raw)
                            else
                                // Regular parameter, use the escaped value
                                Some(key, value.Escaped))
                    // Sort parameters alphabetically by key to match MSBuild behavior
                    |> List.sortBy fst

                String.Join(", ", List.map (fun (key, value) -> sprintf "%s = %s" key value) processedNamedParameters)
            // construct the final argument string; positional arguments followed by named
            match (combinedOrderedParameters.Length, combinedNamedParameters.Length) with
            | (0, 0) -> "" // no arguments
            | (0, _) -> combinedNamedParameters // only named arguments
            | (_, 0) -> combinedOrderedParameters // only positional arguments
            | (_, _) -> combinedOrderedParameters + ", " + combinedNamedParameters // both positional and named arguments

        match language.ToLowerInvariant() with
        | "f#" -> sprintf "[<assembly: %s(%s)>]" attributeName args
        | "c#" -> sprintf "[assembly: %s(%s)]" attributeName args
        | "vb" -> sprintf "<Assembly: %s(%s)>" attributeName args
        | _ -> failTask "Language name must be one of F#, C# or VB"

    // adding this property to maintain API equivalence with the MSBuild task
    member _.Language
        with get () = _language
        and set (value) = _language <- value

    member _.AssemblyAttributes
        with get () = _assemblyAttributes
        and set (value) = _assemblyAttributes <- value

    member _.OutputDirectory
        with get () = _outputDirectory
        and set (value) = _outputDirectory <- value

    [<Output>]
    member _.OutputFile
        with get () = _outputFile
        and set (value) = _outputFile <- value

    override this.Execute() =
        try
            match _outputFile with
            | Null -> failTask "Output location must be specified"
            | NonNull outputFile ->
                let boilerplate =
                    match _language.ToLowerInvariant() with
                    | "f#" ->
                        "// <auto-generated>\n//     Generated by the FSharp WriteCodeFragment class.\n// </auto-generated>\nnamespace FSharp\n\nopen System\nopen System.Reflection\n"
                    | "c#" ->
                        "// <auto-generated>\n//     Generated by the FSharp WriteCodeFragment class.\n// </auto-generated>\n\nusing System;\nusing System.Reflection;"
                    | "vb" ->
                        "'------------------------------------------------------------------------------\n' <auto-generated>\n'     Generated by the FSharp WriteCodeFragment class.\n' </auto-generated>\n'------------------------------------------------------------------------------\n\nOption Strict Off\nOption Explicit On\n\nImports System\nImports System.Reflection"
                    | _ -> failTask "Language name must be one of F#, C# or VB"

                let sb = StringBuilder().AppendLine(boilerplate).AppendLine()

                let code =
                    (sb, _assemblyAttributes)
                    ||> Array.fold (fun (sb: StringBuilder) (item: ITaskItem) ->
                        sb.AppendLine(this.GenerateAttribute(item, _language.ToLowerInvariant())))

                if _language.ToLowerInvariant() = "f#" then
                    code.AppendLine("do()") |> ignore

                let fileName = outputFile.ItemSpec

                let outputFileItem =
                    match _outputDirectory with
                    | Null -> outputFile
                    | NonNull outputDirectory ->
                        if Path.IsPathRooted(fileName) then
                            outputFile
                        else
                            TaskItem(Path.Combine(outputDirectory.ItemSpec, fileName)) :> ITaskItem

                let codeText = code.ToString()
                File.WriteAllText(fileName, codeText)
                _outputFile <- outputFileItem
                not this.Log.HasLoggedErrors
        with TaskFailed ->
            false
