# Padlume

App de desktop (Windows, WPF/.NET 8) que lista os controles conectados no PC — via Bluetooth (ex.: adaptador TP-Link UB500) ou USB — deixa você escolher qual controle acompanhar e mostra o nível de bateria em tempo real.

## Por que pede Administrador

O Padlume pede elevação (Administrador) por dois motivos, ambos opcionais (o app funciona sem admin também,
só com essas duas funções desligadas):

- **"Iniciar automaticamente com o Windows"** (`Services/StartupManager.cs`) usa uma Tarefa Agendada do
  Windows com privilégio máximo (`schtasks /create ... /rl highest`) em vez da chave de registro clássica —
  necessário porque um app elevado colocado na chave `Run` simplesmente não abre sozinho no login (o Windows
  não eleva itens de inicialização automaticamente).
- **"Permitir controlar pelo celular"** (`Services/RemoteControlServer.cs`) cria/remove uma regra no Firewall
  do Windows (`netsh advfirewall`) pra liberar a porta local usada pela página do celular.

Nenhuma das duas funções desabilita ou mexe em outro dispositivo/hardware do sistema — o app apenas lista os
controles detectados via `Windows.Gaming.Input.RawGameController` (API padrão do Windows) e lê a bateria
deles.

- O app é open source — todo o código que roda com esse privilégio está neste repositório.
- Os binários publicados (`Setup.exe`, na raiz do repositório) são gerados por um workflow do GitHub Actions
  a partir deste código-fonte público (ver `.github/workflows/`), não à mão — então o que você baixa numa
  Release corresponde ao que está no repositório nessa tag.

## Como funciona

Usa a API `Windows.Gaming.Input.RawGameController` do próprio Windows, a mesma que o sistema usa para reportar bateria de controles (ex.: no Configurações > Bluetooth e dispositivos). Ela enxerga qualquer controle reconhecido como game controller pelo Windows, independente do adaptador Bluetooth usado.

## Requisitos

- Windows 10 (1809+) ou Windows 11
- .NET 8 SDK (https://dotnet.microsoft.com/download)
- Controle já pareado em Configurações > Bluetooth e dispositivos > Adicionar dispositivo

## Instalação

Baixe e rode `Setup.exe` (raiz do repositório) — instala em Arquivos de Programas, cria atalho no
menu Iniciar (e, opcionalmente, na área de trabalho) e registra o desinstalador no Painel de Controle.
Pede elevação (Administrador) na instalação; veja a seção acima ("Por que pede Administrador") pra
entender exatamente pra que isso é usado.

Pra gerar o instalador depois de alterar o código:

```
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishReadyToRun=false -o publish
"%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe" installer\Padlume.iss
```

## Como rodar a partir do código-fonte (desenvolvimento)

1. Copie a pasta do projeto para o Windows.
2. Pareie o controle no Bluetooth do Windows normalmente (ligue o controle em modo de pareamento e emparelhe com o adaptador TP-Link UB500).
3. Abra um terminal na pasta do projeto e rode:

```
dotnet run
```

Ou abra `Padlume.csproj` no Visual Studio 2022+ e aperte F5.

## Uso

- A lista mostra todos os controles detectados.
- Clique em um controle para ver o nível de bateria (atualiza sozinho a cada 3s).
- "Atualizar lista" força uma nova varredura (útil ao ligar/pareiar um controle novo).
- Fechar a janela minimiza para a bandeja do sistema — o app continua rodando. Clique no ícone da bandeja para reabrir ou sair de vez.

## Limitação importante

Nem todo controle Bluetooth expõe o nível de bateria para o Windows — isso depende do driver/firmware do controle, não do app:

- **Controles Xbox sem fio**: reportam bateria de forma confiável.
- **Outros controles (PlayStation, genéricos, 8BitDo, etc.)**: se o driver do Windows não expuser bateria, o app mostra "N/D" nesse controle. Nesse caso normalmente é preciso um driver/app do fabricante para ler a bateria.
