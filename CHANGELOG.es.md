# MilfTV - Notas de la versión

[English](CHANGELOG.md) | [Français](CHANGELOG.fr.md) | [Español](CHANGELOG.es.md) | [Deutsch](CHANGELOG.de.md) | [Italiano](CHANGELOG.it.md) | [Português](CHANGELOG.pt.md)

Todos los cambios destacados de MilfTV, del más reciente al más antiguo.

## 1.0.0-beta.1

*Publicada el 2026-11-23*

Primera beta pública de MilfTV, un reproductor IPTV para Windows. Esta versión reúne todo lo construido hasta ahora; aquí tienes un recorrido completo por lo que sabe hacer. Como toda beta, aún puede contener errores: notifícalos en la pestaña Issues de GitHub.

### Fuentes IPTV

- Cuentas Xtream Codes (servidor, usuario, contraseña), listas M3U/M3U8 (enlace web o archivo local) y portales MAC/Stalker (beta).
- Cada perfil tiene sus propias fuentes, privadas o compartidas con los demás perfiles; solo el perfil que añadió una fuente puede modificarla.
- Un perfil sin ninguna fuente recibe automáticamente la lista gratuita «TV gratuite France» (unos 230 canales franceses gratuitos del proyecto iptv-org).
- Las contraseñas de las fuentes se cifran con tu cuenta de Windows.

### TV en directo y guía de TV

- Canales por categoría con el programa en curso y su progreso; ficha del canal con lo que se emite ahora y a continuación.
- Guía de TV (XMLTV) en cuadrícula, de la propia fuente o de una dirección que elijas.
- Directo en diferido: pausa, retrocede y vuelve al directo (memoria de 60 minutos por defecto).
- Canales favoritos y canales vistos recientemente.

### Películas y series

- Cuadrículas de pósteres con categorías, orden y filtro Todo / Sin ver / Vistas, además de una insignia «Visto» y una barra de progreso.
- Fichas con sinopsis, reparto, nota y tráiler, enriquecidas con TMDB (clave gratuita que se introduce en Ajustes).
- Series: temporadas y episodios con miniaturas; el siguiente episodio empieza solo, con cuenta atrás y la pregunta «¿Sigues ahí?».
- Deja el puntero un momento sobre un póster para ver una vista previa con su tráiler, un botón de sonido y un control de volumen.
- La reproducción se reanuda donde la dejaste; historial y Mi lista para cada perfil.

### Reproductor

- Motor mpv: decodificación por hardware, fuentes 4K y HDR, modo «Imagen de alta calidad» con reducción de bandas en los degradados.
- Visualización fluida que sigue la frecuencia de la pantalla, con vuelta automática a un modo compatible en los PC donde no funciona.
- Pista de audio, subtítulos, dispositivo de salida de audio, formato de imagen, velocidad y volumen hasta el 150 %.
- Menú del clic derecho sobre el vídeo y panel de información de reproducción en directo (códec, resolución, tasa de bits, fotogramas perdidos…).
- Arrastra el vídeo para mover la ventana, doble clic para pantalla completa, atajos de teclado (Espacio, flechas, F, M, R…).
- Los directos que se cortan se reabren automáticamente.

### Minirreproductor

- El vídeo sigue reproduciéndose en una ventana flotante mientras navegas por la aplicación.
- Muévelo donde quieras y cambia su tamaño por los bordes y esquinas; al pasar el puntero aparecen sus controles (reproducir/pausa, saltos, sonido, volver al reproductor completo, detener).

### Grabaciones

- Grabación inmediata desde el reproductor y grabaciones programadas desde la guía de TV.
- Formato TS, MKV o MP4, elección de calidad para flujos de varias tasas de bits, nombres de archivo a tu gusto, parada automática al final del programa.
- Salvaguardas: espacio mínimo en disco, límite de grabaciones simultáneas (las siguientes esperan su turno), PC despierto, confirmación antes de cerrar, aviso si el archivo deja de crecer.

### Perfiles y control parental

- Varios perfiles, cada uno con su avatar (emoji o imagen), su color, una contraseña opcional y sus preferencias (tema, idioma, panel de inicio, historial…).
- Perfiles infantiles: contenido adulto oculto, código parental necesario para cambiar los ajustes protegidos o el tipo de perfil.
- Categorías bloqueadas, bloqueo automático de las categorías para adultos, ajustes protegidos por el código.
- Menú del perfil abajo a la izquierda: editar el perfil, cambiar de perfil, cerrar sesión.

### Interfaz

- Disponible en 6 idiomas: francés, inglés, español, alemán, italiano y portugués.
- Temas claro y oscuro con color a elegir; panel de inicio personalizable.
- Búsqueda en canales, películas, series y programas (Ctrl+F).
- Ajustes organizados por secciones, con buscador.

### Instalación, actualizaciones y copias de seguridad

- Instalador en un clic, sin derechos de administrador; también hay una versión portátil.
- Actualizaciones automáticas desde GitHub, instaladas al cerrar MilfTV para no interrumpir nada; canales Estable y Beta, con vuelta a Estable en cualquier momento.
- Copia de seguridad y restauración de la configuración (perfiles, fuentes, ajustes, historial, Mi lista…), con contraseña opcional para llevarla a otro PC.
- Notas de la versión en la aplicación (Ajustes › Datos y acerca de › Notas de la versión).

### Limitaciones conocidas

- Solo Windows 10 y 11 de 64 bits.
- El contenido HDR se muestra en SDR (con conversión de tonos).
- El instalador aún no está firmado: en el primer inicio, Windows SmartScreen puede mostrar un aviso (haz clic en «Más información» y luego en «Ejecutar de todas formas»).
- Las grabaciones solo se hacen mientras MilfTV está abierto.
