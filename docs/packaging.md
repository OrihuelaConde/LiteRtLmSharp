# Empaquetado y consumo NuGet

Modelo estilo LLamaSharp: **un paquete managed puro + paquetes de runtime nativo por-RID**.

| Paquete | Contenido | TFM |
|---|---|---|
| `LiteLMSharp` | Solo el assembly managed (`lib/net10.0/LiteLMSharp.dll`). Sin nativos. | net10.0 |
| `LiteLMSharp.runtime.win-x64` | `runtimes/win-x64/native/*.dll` (LiteRtLm + companions + DXC). Sin lib. | (native-only) |
| `LiteLMSharp.runtime.linux-x64` | `runtimes/linux-x64/native/*.so`. Sin lib. | (native-only) |

Las **versiones van juntas** y mapean al tag de LiteRT-LM (ver `Directory.Build.props`:
`Version` ↔ `LiteRtLmVersion`). Hoy: `0.13.1-preview.1` ↔ `v0.13.1`.

## Consumo (incluido MAUI)

```xml
<PackageReference Include="LiteLMSharp" Version="0.13.1-preview.1" />
<PackageReference Include="LiteLMSharp.runtime.win-x64" Version="0.13.1-preview.1" />
<!-- y/o linux-x64 según el target -->
```

El SDK copia `runtimes/<rid>/native/*` al output del consumidor; `NativeLibraryResolver` los resuelve
(prueba: la app demo ↑ cargó y generó solo desde los paquetes). El managed package **no** depende de un
runtime package (cada consumidor elige su RID, como LLamaSharp).

> Validado end-to-end localmente: managed + runtime.win-x64 desde un feed local → la app consumidora
> resolvió los nativos y corrió inferencia (`Consumer says: Hello, how are you?`).

## Generación de paquetes

1. **Build nativo** (`build-native.yml`) pinneado a un tag (`v0.13.1`) con `publish_release` → crea el
   release `native-v0.13.1` con `litertlm-{windows,linux}_x86_64.tar.gz`.
2. **Pack** (`pack-nuget.yml`): baja esos assets, los acomoda en `runtimes/<rid>/native/`, hace
   `dotnet pack` de los 3 proyectos con la versión dada, sube los `.nupkg` como artifact, y
   (opcional, `push=true` + secret `NUGET_API_KEY`) publica a nuget.org.

Local: `dotnet pack LiteLMSharp/LiteLMSharp.csproj -c Release -o .nupkgs` (managed) y los proyectos en
`packaging/` (requieren los nativos ya presentes en `runtimes/<rid>/native/`).

## Notas / pendientes

- **Repo privado**: `scripts/restore-natives.ps1` (dev local) no puede bajar releases privados sin token.
  En CI funciona (usa `GITHUB_TOKEN`/`gh`). Para dev local con repo privado: `gh release download` o un PAT.
  Al hacer el repo público (o publicar a nuget.org), el consumo es directo.
- **MSVC runtime**: `LiteRtLm.dll` (win-x64) importa `MSVCP140/VCRUNTIME140*`; depende del VC++ Redistributable
  en la máquina del usuario. A futuro: documentarlo como prerequisito o evaluar shippearlo.
- Próximos RIDs (Fase 3): `linux-arm64`, `android-arm64`, `ios-arm64` (mismo patrón de paquete runtime).
- Opcional a futuro: meta-paquete `LiteLMSharp.Backend.Desktop` que dependa de los runtime win/linux.
