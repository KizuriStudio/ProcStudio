# ProcStudio

Monitor de processos avancado para Windows, focado em observabilidade local, investigacao operacional e triagem de comportamento anormal de processos.

## Stack

- `C# / .NET 8`
- `WPF` para desktop Windows
- `System.Diagnostics` + `WMI`
- `P/Invoke` para memoria virtual, tokens e tabelas de rede
- `GitHub Actions` para build, teste e publish no Windows

## Recursos implementados

- Interface desktop escura com foco em densidade de informacao
- Atualizacao automatica a cada 2 segundos
- Arvore interativa de processos baseada em `PID -> Parent PID`
- Tabela principal com CPU, RAM, GPU, handles, assinatura e elevacao
- Pesquisa por nome, PID, empresa, usuario e linha de comando
- Perfis salvos para CPU alta, memoria alta, assinados e navegadores
- Snapshot de threads com CPU individual, prioridade e estado
- Listagem de DLLs/modulos carregados com verificacao de assinatura digital
- Conexoes TCP e UDP por processo
- Mapa de memoria baseado em `VirtualQueryEx`
- Timeline por processo com amostras de CPU e RAM
- Alertas locais por regra de CPU, RAM, GPU e handles
- Exportacao de relatorios em `JSON`, `CSV` e `HTML`

## Estrutura

- `src/ProcStudio.App`: UI WPF, composicao da janela principal e fluxo de exportacao
- `src/ProcStudio.Core`: contratos, modelos, timeline, alertas, arvore e exporters
- `src/ProcStudio.Infrastructure`: coleta real do Windows
- `tests/ProcStudio.Tests`: testes de regressao dos componentes centrais
- `.github/workflows/build-windows.yml`: pipeline de CI para compilar no GitHub

## Como compilar localmente

Requisitos:

- Windows 10/11
- .NET SDK 8
- Visual Studio 2022 ou Build Tools com workload desktop

Comandos:

```powershell
dotnet restore ProcStudio.sln
dotnet build ProcStudio.sln -c Release
dotnet test ProcStudio.sln -c Release
dotnet run --project .\src\ProcStudio.App\ProcStudio.App.csproj
```

Para gerar uma versao final sem dependencia de .NET no computador do usuario:

```powershell
dotnet publish .\src\ProcStudio.App\ProcStudio.App.csproj `
  -c Release `
  -f net8.0-windows10.0.19041.0 `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:PublishReadyToRun=true `
  -o .\artifacts\publish\win-x64
```

## Pipeline GitHub

O workflow em `.github/workflows/build-windows.yml`:

- restaura dependencias
- compila toda a solucao em `Release`
- executa testes
- publica a aplicacao `win-x64` em modo `self-contained`
- empacota a distribuicao em `ProcStudio-win-x64-self-contained.zip`
- envia o `.zip` final como artifact do workflow

## Proximos upgrades de nivel profissional

- ETW para timeline de eventos ainda mais rica
- correlacao de criacao/encerramento de processos em tempo real
- detalhamento de handles via `NtQuerySystemInformation`
- geolocalizacao externa opcional para IPs publicos
- grafico de timeline com renderizacao vetorial dedicada
- comparacao de snapshots e diff historico
- regras de alerta persistidas por usuario
