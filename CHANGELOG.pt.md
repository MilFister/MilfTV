# MilfTV - Notas da versão

[English](CHANGELOG.md) | [Français](CHANGELOG.fr.md) | [Español](CHANGELOG.es.md) | [Deutsch](CHANGELOG.de.md) | [Italiano](CHANGELOG.it.md) | [Português](CHANGELOG.pt.md)

Todas as alterações relevantes do MilfTV, da mais recente para a mais antiga.

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
