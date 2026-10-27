# Simulador de punción lumbar en realidad virtual con retroalimentación háptica

Implementación en Unity del simulador desarrollado para el Proyecto Final de
Ingeniería «Diseño, Implementación y Evaluación Preliminar de un Simulador de
Punción Lumbar Basado en Realidad Virtual y Retroalimentación Háptica»,
Bioingeniería, Facultad de Ingeniería, Universidad Nacional de Entre Ríos (UNER),
Oro Verde, Argentina.

- **Autores:** Facundo Nahuel Schneider, Lucio Joaquín Sepúlveda Villabona
- **Director:** Dr. Bioing. José Biurrun Manresa

## Descripción

El simulador plantea un entorno de realidad virtual en el que el usuario practica una
punción lumbar sobre un modelo anatómico reconstruido a partir de una segmentación
médica real. El dispositivo háptico reproduce la resistencia de cada tejido que
atraviesa la aguja, desde la piel hasta el espacio subaracnoideo.

La anatomía (vértebras L1 a S1, médula espinal, músculos profundos de la espalda,
psoas ilíaco, grasa y piel) se genera por voxelización de un mapa de etiquetas en
formato `.nrrd` dentro de Unity, con una malla independiente por estructura y
visibilidad configurable. El módulo responsable es `Assets/SpineVoxels/`.

La simulación se organiza en dos etapas:

1. **Palpación.** El usuario localiza y marca el punto de inserción por tacto sobre
   las apófisis espinosas; a continuación el sistema informa la distancia respecto
   del punto ideal.
2. **Punción.** Se inserta la aguja y un modelo de fuerzas por capas reproduce la
   resistencia de cada tejido, a partir de curvas calibradas que se almacenan en
   archivos JSON, acompañado de señales de audio y vibración. Cada sesión queda
   registrada (tiempo, velocidad y fuerza axial, tejido atravesado) y puede
   exportarse a CSV.

## Requisitos

### Software

- Unity 6000.4.1f1 con Universal Render Pipeline (URP). No se garantiza el
  funcionamiento en otras versiones.
- Paquetes principales, que el Package Manager resuelve automáticamente al abrir el
  proyecto:
  - Meta XR All-in-One SDK 203.0.0 con OpenXR.
  - Haply Inverse for Unity 3.2.2 (el registro con alcance propio de Haply ya está
    declarado en `Packages/manifest.json`).
  - Universal Render Pipeline, Input System y NativeWebSocket.

### Hardware

- Visor Meta Quest 2 o Quest 3, conectado por Oculus Link o Air Link (la aplicación
  se ejecuta en la PC).
- Dispositivo háptico Haply Inverse3 con mango VerseGrip.

El proyecto incluye una escena con realidad virtual y otra sin ella. Tanto la
palpación como la punción pueden ejercitarse con el Inverse3, y algunas acciones
cuentan con una alternativa mediante el mouse.

## Puesta en marcha

1. Instalar Unity 6000.4.1f1 desde Unity Hub.
2. Clonar este repositorio y abrirlo con esa versión de Unity. Al abrirlo, el Package
   Manager descarga automáticamente las dependencias, lo que puede demorar la primera
   vez.
3. Conectar y encender el Inverse3, con el runtime de Haply instalado, y, para el uso
   en realidad virtual, el Quest mediante Link o Air Link.
4. Abrir una de las escenas de `Assets/Scenes/`:
   - `PuncionConVR.unity`, experiencia completa en realidad virtual.
   - `PuncionSinVR.unity`, versión de escritorio sin visor, útil para el desarrollo y
     para ejercitar la háptica de forma aislada.
5. Presionar Play.

Los registros de sesión en formato CSV se guardan en `Application.persistentDataPath`.

## Estructura del proyecto

```
Assets/
├── Scenes/            PuncionConVR, PuncionSinVR
├── SpineVoxels/       Núcleo del simulador
│   ├── Core/          Lectura del NRRD, voxelización y construcción de mallas
│   ├── Config/        Paleta de estructuras y parámetros de construcción (ScriptableObjects)
│   ├── Editor/        Herramientas de generación del modelo en el editor
│   ├── Runtime/       Háptica, modelo de fuerzas, etapas y registro de sesión
│   └── PythonIntegration/  Cliente WebSocket para la calibración de fuerzas
├── StreamingAssets/   Perfiles de fuerza por capa (JSON)
├── Haply/, Oculus/    SDKs de terceros
└── ...
```

El archivo `Assets/SpineVoxels/SPEC.md` documenta el pipeline de voxelización.

## Herramienta de calibración

Las curvas de fuerza por tejido que utiliza el simulador se calibran con una
aplicación de escritorio desarrollada en Python (PySide6) como parte del mismo
proyecto. Esa aplicación permite ajustar de forma interactiva la respuesta mecánica de
cada capa, exporta los perfiles en formato JSON y puede transmitir la configuración al
simulador en tiempo real por WebSocket. Se encuentra en el repositorio
<https://github.com/Luciosv/calibracion-puncion-lumbar>.

## Cómo citar

> Schneider, F. N. y Sepúlveda Villabona, L. J. (2026). *Diseño, Implementación y
> Evaluación Preliminar de un Simulador de Punción Lumbar Basado en Realidad Virtual
> y Retroalimentación Háptica* [Proyecto Final de Ingeniería]. Universidad Nacional
> de Entre Ríos, Facultad de Ingeniería, Oro Verde, Argentina.

## Licencia

© 2026 Facundo N. Schneider y Lucio J. Sepúlveda Villabona.

Publicado bajo Creative Commons Atribución-NoComercial-SinDerivadas 4.0 Internacional
(CC BY-NC-ND 4.0). Se permite ver y compartir el material citando a los autores. No se
autoriza el uso comercial ni la distribución de obras derivadas sin permiso previo.
Para otros usos, consultar con los autores. Texto completo en
<https://creativecommons.org/licenses/by-nc-nd/4.0/deed.es>.

Los SDKs y paquetes de terceros incluidos (Meta XR, Haply Inverse y otros) se rigen
por sus propias licencias.
