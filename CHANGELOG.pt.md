# MilfTV - Notas da versão

[English](CHANGELOG.md) | [Français](CHANGELOG.fr.md) | [Español](CHANGELOG.es.md) | [Deutsch](CHANGELOG.de.md) | [Italiano](CHANGELOG.it.md) | [Português](CHANGELOG.pt.md)

Todas as alterações relevantes do MilfTV, da mais recente para a mais antiga.

## 1.0.0-beta.2

*Publicada em 2026-11-24*

Atualização de segurança após uma auditoria completa da aplicação, com novidades: informações do conteúdo nas fichas e pré-visualizações, ficha de programas ao passar sobre os canais, temporada ou série inteira marcada como vista de uma só vez, atalho no ambiente de trabalho opcional e uma versão nativa para PC Windows em ARM. É recomendada a todos.

### Novidades

- Na lista de canais do leitor, deixe o rato um momento sobre um canal para ver a sua ficha: o programa em curso (horário, progresso, tempo restante, género, episódio, sinopse e imagem quando o guia os fornece) e os programas seguintes. Segue a definição «Pré-visualização ao passar o rato» e o seu atraso, como os cartazes.
- Séries: marque de uma só vez uma temporada inteira ou toda a série como vista (ou não vista), com os botões da ficha da série. O menu de contexto do cartaz de uma série também oferece «Marcar toda a série como vista». O botão de reprodução recomeça então no primeiro episódio não visto.
- Instalação: o atalho no ambiente de trabalho deixou de ser imposto. No primeiro arranque, o MilfTV pergunta se quer um, e pode adicioná-lo ou removê-lo a qualquer momento em Definições › Dados e acerca de. É removido ao desinstalar o MilfTV. O atalho do menu Iniciar continua a ser criado.
- Início: as filas horizontais deslocam-se com mais facilidade. A barra de deslocamento foi redesenhada com as cores da aplicação (uma pega na cor de destaque que engrossa ao passar o rato) e está sempre visível, aparecem setas ao passar o rato para avançar um ecrã, e pode arrastar uma fila para o lado com o rato (botão esquerdo premido, mesmo sobre um cartaz). O mesmo vale para Elenco e Na mesma categoria nas fichas.
- Minileitor: ajuste o volume diretamente, com um cursor ao lado do botão de som (e a percentagem quando há espaço), ou com a roda do rato sobre o minileitor.
- Fichas de filmes e séries: informações do conteúdo. Etiquetas sob o título (4K, HDR, HEVC, 5.1, idiomas) e uma secção Informações do conteúdo: resolução, vídeo, áudio, idiomas de áudio, legendas, duração, taxa de bits, formato e idioma original. O seu fornecedor indica-as e a primeira reprodução completa-as (todos os idiomas de áudio e as legendas). Nas séries, cada episódio tem a sua linha técnica e a ficha resume a temporada.
- Todas as barras de deslocamento da aplicação adotam o novo estilo: uma pista discreta e uma pega na cor de destaque que engrossa ao passar o rato.
- Pré-visualização ao passar sobre os cartazes: mostra também as informações do conteúdo (resolução, HDR, codec, som, idiomas) e, nas séries, a duração de um episódio.
- Reprodução automática: escolha quando começa a contagem decrescente do episódio seguinte (Definições › Reprodução automática): no fim do episódio, como antes, ou antes do fim, durante o genérico, para que o episódio seguinte comece assim que este acaba. A contagem pode agora durar até 60 segundos.
- Windows em ARM: uma versão ARM64 nativa para PC com processadores Snapdragon e outros chips ARM, com o seu próprio instalador e as suas próprias atualizações automáticas.

### Segurança

- Os endereços de fluxo das listas e portais passam a ser verificados: as partilhas de rede e os protocolos internos do motor de vídeo são recusados. Uma lista maliciosa já não consegue fazer o Windows enviar a impressão digital da palavra-passe da sua conta para um servidor desconhecido.
- Os certificados HTTPS dos servidores IPTV passam a ser verificados. Se o seu fornecedor usar um certificado inválido, marque «Aceitar um certificado inválido» nas opções avançadas da fonte (não recomendado).
- Os utilizadores, palavras-passe e tokens são ocultados no registo, que pode assim ser anexado a um relatório de erro.
- O código parental é guardado como impressão digital com sal, e os erros repetidos de código ou de palavra-passe de perfil impõem agora uma espera crescente (de 5 segundos a 5 minutos).
- Listas, catálogos ou guias de TV desmesurados são recusados em vez de saturarem a memória.
- O leitor de trailers recusa transferências e pedidos de câmara, microfone, localização ou notificações.
- As ferramentas de teste já não são incluídas nas versões publicadas.
- Antes de um restauro, são mostrados os servidores das fontes da cópia e, se existir, a sua pasta de rede de gravações.

### Correções

- O título «Quem está a ver?» acompanha a mudança de idioma.
- Em A minha lista, «Marcar como visto» não fazia nada numa série: agora marca todos os seus episódios.

## 1.0.0-beta.1

*Publicada em 2026-11-23*

Primeira beta pública do MilfTV, um leitor IPTV para Windows. Esta versão reúne tudo o que foi construído até agora; eis uma visita completa ao que sabe fazer. Como qualquer beta, ainda pode conter erros: comunique-os no separador Issues do GitHub.

### Fontes IPTV

- Contas Xtream Codes (servidor, utilizador, palavra-passe), listas M3U/M3U8 (ligação web ou ficheiro local) e portais MAC/Stalker (beta).
- Cada perfil tem as suas próprias fontes, privadas ou partilhadas com os outros perfis; só o perfil que adicionou uma fonte a pode alterar.
- Um perfil sem nenhuma fonte recebe automaticamente a lista gratuita «TV gratuite France» (cerca de 230 canais franceses gratuitos do projeto iptv-org).
- As palavras-passe das fontes são cifradas com a sua conta Windows.

### TV em direto e guia de TV

- Canais por categoria com o programa em curso e o seu progresso; ficha do canal com o que está a dar agora e a seguir.
- Guia de TV (XMLTV) em grelha, da própria fonte ou de um endereço à sua escolha.
- Diferido: pause, recue e volte ao direto (memória de 60 minutos por predefinição).
- Canais favoritos e canais vistos recentemente.

### Filmes e séries

- Grelhas de cartazes com categorias, ordenação e filtro Todos / Ainda não vistos / Vistos, além de um selo «Visto» e de uma barra de progresso.
- Fichas com sinopse, elenco, classificação e trailer, enriquecidas pelo TMDB (chave gratuita a introduzir nas Definições).
- Séries: temporadas e episódios com miniaturas; o episódio seguinte começa sozinho, com contagem decrescente e a pergunta «Ainda está aí?».
- Deixe o ponteiro um momento sobre um cartaz para ver uma pré-visualização com o trailer, um botão de som e um controlo de volume.
- A reprodução retoma onde parou; histórico e A minha lista para cada perfil.

### Leitor

- Motor mpv: descodificação por hardware, fontes 4K e HDR, modo «Imagem de alta qualidade» com redução de bandas nos gradientes.
- Visualização fluida que acompanha a frequência do ecrã, com regresso automático a um modo compatível nos PC onde não funciona.
- Faixa de áudio, legendas, dispositivo de saída de áudio, formato de imagem, velocidade e volume até 150 %.
- Menu do clique direito no vídeo e painel de informações de reprodução em tempo real (codec, resolução, débito, imagens perdidas…).
- Arraste o vídeo para mover a janela, faça duplo clique para ecrã inteiro, atalhos de teclado (Espaço, setas, F, M, R…).
- Os diretos que caem são reabertos automaticamente.

### Minileitor

- O vídeo continua numa janela flutuante enquanto navega na aplicação.
- Mova-o para onde quiser e redimensione-o pelas margens e cantos; ao passar o ponteiro surgem os comandos (reproduzir/pausa, saltos, som, voltar ao leitor completo, parar).

### Gravações

- Gravação imediata a partir do leitor e gravações agendadas a partir do guia de TV.
- Formato TS, MKV ou MP4, escolha da qualidade para fluxos com vários débitos, nomes de ficheiro à sua escolha, paragem automática no fim do programa.
- Salvaguardas: espaço mínimo em disco, limite de gravações simultâneas (as seguintes aguardam a vez), PC mantido ativo, confirmação antes de fechar, alerta se o ficheiro deixar de crescer.

### Perfis e controlo parental

- Vários perfis, cada um com avatar (emoji ou imagem), cor, palavra-passe opcional e preferências próprias (tema, idioma, painel inicial, histórico…).
- Perfis infantis: conteúdos para adultos ocultos, código parental exigido para alterar as definições protegidas ou o tipo de perfil.
- Categorias bloqueadas, bloqueio automático das categorias para adultos, definições protegidas pelo código.
- Menu do perfil em baixo à esquerda: editar o perfil, mudar de perfil, terminar sessão.

### Interface

- Disponível em 6 idiomas: francês, inglês, espanhol, alemão, italiano e português.
- Temas claro e escuro com cor à escolha; painel inicial personalizável.
- Pesquisa em canais, filmes, séries e programas (Ctrl+F).
- Definições organizadas em secções, com pesquisa.

### Instalação, atualizações e cópia de segurança

- Instalador num clique, sem direitos de administrador; também existe uma versão portátil.
- Atualizações automáticas a partir do GitHub, instaladas ao fechar o MilfTV para não interromper nada; canais Estável e Beta, com regresso ao Estável a qualquer momento.
- Cópia de segurança e restauro da configuração (perfis, fontes, definições, histórico, A minha lista…), com palavra-passe opcional para a levar para outro PC.
- Notas da versão na aplicação (Definições › Dados e acerca de › Notas da versão).

### Limitações conhecidas

- Apenas Windows 10 e 11 de 64 bits.
- Os conteúdos HDR são apresentados em SDR (com conversão de tons).
- O instalador ainda não está assinado: no primeiro arranque, o Windows SmartScreen pode mostrar um aviso (clique em «Mais informações» e depois em «Executar mesmo assim»).
- As gravações só são feitas enquanto o MilfTV estiver aberto.
