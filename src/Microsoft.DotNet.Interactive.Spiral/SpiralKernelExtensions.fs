﻿// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.DotNet.Interactive.Spiral

open System
open System.Runtime.CompilerServices
open Microsoft.AspNetCore.Html
open Microsoft.DotNet.Interactive
open Microsoft.DotNet.Interactive.Commands
open Microsoft.DotNet.Interactive.Spiral
open Microsoft.DotNet.Interactive.Formatting

open System.IO

open Polyglot.Common

[<AbstractClass; Extension; Sealed>]
type SpiralKernelExtensions private () =

    static let referenceAssemblyContaining (typ: Type) =
        trace Verbose (fun () -> $"referenceAssemblyContaining / typ: %A{typ}") _locals
        sprintf "#r \"%s\"" (typ.Assembly.Location.Replace("\\", "/"))
    static let openNamespaceContaining (typ: Type) =
        trace Verbose (fun () -> $"openNamespaceContaining / typ: %A{typ}") _locals
        sprintf "open %s" typ.Namespace
    static let openType (typ: Type) =
        trace Verbose (fun () -> $"openType / typ: %A{typ}") _locals
        sprintf "open type %s.%s" typ.Namespace typ.Name

    [<Extension>]
    static member UseDefaultFormatting(kernel: SpiralKernel) =
        trace Verbose (fun () -> $"UseDefaultFormatting") _locals
        let code =
            [
                referenceAssemblyContaining typeof<IHtmlContent>
                referenceAssemblyContaining typeof<Kernel>
                referenceAssemblyContaining typeof<SpiralKernelHelpers.IMarker>
                referenceAssemblyContaining typeof<Formatter>

                openNamespaceContaining typeof<System.Console>
                openNamespaceContaining typeof<System.IO.File>
                openNamespaceContaining typeof<System.Text.StringBuilder>
                openNamespaceContaining typeof<Formatter>
            ] |> String.concat Environment.NewLine

        kernel.DeferCommand(SubmitCode code)
        kernel

    [<Extension>]
    static member UseKernelHelpers(kernel: SpiralKernel) =
        trace Verbose (fun () -> $"UseKernelHelpers") _locals
        let code =
            [
                referenceAssemblyContaining typeof<SpiralKernelHelpers.IMarker>

                // opens Microsoft.DotNet.Interactive.Spiral.SpiralKernelHelpers
                //    note this has some AutoOpen content inside
                openNamespaceContaining typeof<SpiralKernelHelpers.IMarker>

                referenceAssemblyContaining typeof<Kernel>
                openType typeof<Kernel>

            ] |> String.concat Environment.NewLine

        kernel.DeferCommand(SubmitCode code)
        kernel
