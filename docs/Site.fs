module Docs.Site

open Feliz.ViewEngine
open Nacara.Core
open Nacara.Plugins
open Nacara.Theme

let versions = [ SiteVersion.root "0.1" ]

let theme =
    Theme.defaults
    |> Theme.navbar [ NavbarSection ("Guide", "guide", "/guide/introduction/") ]
    |> Theme.navbarEnd
        [
            NavbarDynamicWidget (Versions.switcher (Versions.versions versions Versions.defaults))
            NavbarIcon ("GitHub", "https://github.com/ranvier-org/Ranvier.Plume", Icons.github)
        ]
    |> Theme.editUrl "https://github.com/ranvier-org/Ranvier.Plume/edit/main/docs"
    |> Theme.footer (Html.p [ Html.text "Built with Nacara" ])

let api =
    { FSharpApi.defaults with
        Root = "reference"
        Title = "API reference"
        Sources =
            [ for name in [ "Ranvier.Plume"; "Ranvier.Plume.View" ] ->
                FSharpApiSource.create (System.IO.Path.Combine (System.AppContext.BaseDirectory, name + ".dll")) ] }

let reference =
    FSharpApi.collection "reference" DocFrontMatter.decoder api
    |> Collection.title _.Title
    |> Collection.layout (Theme.layout theme)

let site =
    Site.create "Ranvier.Plume"
    |> Site.baseUrl "/Ranvier.Plume/"
    |> Site.origin "https://ranvier-org.github.io"
    |> Site.output "output"
    |> Markdown.register
    |> TreeSitter.register
    |> Literate.register
    |> AgentFriendly.registerWith (AgentFriendly.summary "Build an F# editor with shared documents, typed shortcuts and virtualised views.")
    |> Site.staticFiles "static"
    |> FSharpApi.register api
    |> Sitemap.register
    |> LinkValidator.registerWith (LinkValidator.checkExternal false)
    |> LightningCss.register
    |> Esbuild.register
    |> Nuglify.minifyHtml
    |> Versions.register versions
    |> GitHubPages.register
    |> Theme.register theme
    |> Site.collection (Theme.docs theme "content")
    |> Site.collection reference

[<EntryPoint>]
let main argv =
    System.IO.Directory.SetCurrentDirectory __SOURCE_DIRECTORY__
    Nacara.run site argv
