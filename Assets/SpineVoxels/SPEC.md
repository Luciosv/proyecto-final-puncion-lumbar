# SpineVoxels — Especificación de implementación

Handoff para implementación. Objetivo: cargar un labelmap de segmentación `.nrrd`
y renderizarlo en Unity como **voxels (cubos visibles)**, una malla por estructura
anatómica, coloreada y con visibilidad togglable. Pipeline 100% C# dentro de Unity,
sin dependencias externas.

> Estilo de implementación: respetar las firmas públicas de este documento. La
> lógica privada es libre mientras cumpla el comportamiento descrito. No introducir
> dependencias de paquetes externos. Render pipeline destino: **URP**.

---

## 1. Datos de entrada (fijos para este archivo)

Header NRRD:

```
type: short              (Int16)
dimension: 3
sizes: 512 512 383       (X, Y, Z) → 100.401.152 voxels
space: left-posterior-superior  (LPS)
space directions: (-0.31919921875,0,0) (0,-0.31919921875,0) (0,0,0.6)
kinds: domain domain domain
endian: little
encoding: gzip
space origin: (64.92740039, 233.00040039, 1621.42)
```

Valores de voxel: `0` = fondo (NO se renderiza), `1..13` = estructuras:

| id | estructura            | id | estructura                 |
|----|-----------------------|----|----------------------------|
| 1  | S1 vertebra           | 8  | left deep back muscle      |
| 2  | L5 vertebra           | 9  | right deep back muscle     |
| 3  | L4 vertebra           | 10 | left iliopsoas muscle      |
| 4  | L3 vertebra           | 11 | right iliopsoas muscle     |
| 5  | L2 vertebra           | 12 | Fat                        |
| 6  | L1 vertebra           | 13 | skin                       |
| 7  | spinal cord           |    |                            |

**No asumir estos números en el código de parseo**: leer `sizes`, `space
directions`, `space origin`, `type`, `endian`, `encoding` del header real. Los
valores de arriba son solo para tests/validación.

---

## 2. Layout de carpetas y asmdefs

```
Assets/SpineVoxels/
├── Core/
│   ├── SpineVoxels.Core.asmdef        (sin referencia a UnityEditor)
│   ├── NrrdHeader.cs
│   ├── NrrdReader.cs
│   ├── LabelVolume.cs
│   ├── VolumeResampler.cs
│   ├── CoordinateTransform.cs
│   ├── VoxelMeshBuilder.cs
│   ├── SpineModelBuilder.cs
│   └── MaterialFactory.cs
├── Config/
│   ├── StructureInfo.cs
│   ├── StructurePalette.cs             (ScriptableObject)
│   └── BuildConfig.cs                  (ScriptableObject)
├── Editor/
│   ├── SpineVoxels.Editor.asmdef       (referencia a Core + Config)
│   └── SpineVoxelBaker.cs
├── Runtime/
│   └── SpineModelRuntimeLoader.cs      (Plan B, opcional)
└── SPEC.md
```

Namespace: `SpineVoxels.Core`, `SpineVoxels.Config`, `SpineVoxels.Editor`,
`SpineVoxels.Runtime`. `Config` puede vivir dentro del asmdef Core para simplificar
(los ScriptableObject no necesitan Editor).

---

## 3. Capa Config

### 3.1 `StructureInfo` (struct serializable)

```csharp
[Serializable]
public struct StructureInfo
{
    public int    labelId;        // 1..13
    public string name;           // "S1", "spinal_cord", ...
    public Color  color;          // color base del material
    public bool   defaultVisible; // false para Fat/skin
    public bool   transparent;    // true para Fat/skin → material URP Transparent
}
```

### 3.2 `StructurePalette : ScriptableObject`

```csharp
[CreateAssetMenu(menuName = "SpineVoxels/Structure Palette")]
public class StructurePalette : ScriptableObject
{
    public List<StructureInfo> structures;   // 13 entradas

    public bool TryGet(int labelId, out StructureInfo info); // false si no existe
    public IEnumerable<int> LabelIds { get; } // ids presentes, orden ascendente
}
```

Valores por defecto a precargar en el asset (color orientativo, ajustable en
inspector):

| id | name                  | color (RGB)      | defaultVisible | transparent |
|----|-----------------------|------------------|----------------|-------------|
| 1  | S1                    | 230,225,200      | true           | false |
| 2  | L5                    | 225,215,180      | true           | false |
| 3  | L4                    | 220,205,165      | true           | false |
| 4  | L3                    | 215,200,160      | true           | false |
| 5  | L2                    | 210,195,150      | true           | false |
| 6  | L1                    | 205,190,140      | true           | false |
| 7  | spinal_cord           | 240,220, 60      | true           | false |
| 8  | left_deep_back_muscle | 200, 60, 60      | true           | false |
| 9  | right_deep_back_muscle| 150, 40, 40      | true           | false |
| 10 | left_iliopsoas        | 210, 70,170      | true           | false |
| 11 | right_iliopsoas       | 140, 50,160      | true           | false |
| 12 | Fat                   | 240,225,120,90   | false          | true  |
| 13 | skin                  | 235,205,170,70   | false          | true  |

### 3.3 `BuildConfig : ScriptableObject`

```csharp
[CreateAssetMenu(menuName = "SpineVoxels/Build Config")]
public class BuildConfig : ScriptableObject
{
    public string          nrrdPath;       // ruta absoluta o relativa a Assets
    public float           voxelSizeMm = 1.5f;   // tamaño del voxel isotrópico de salida
    public float           unityUnitsPerMm = 1f; // 1 = (1 u = 1 mm); 0.001 = metros
    public bool            centerModel = true;   // centrar bounds en el origen
    public ResampleRule    resampleRule = ResampleRule.Majority;
    public StructurePalette palette;
}

public enum ResampleRule { Majority, AnyPresent }
```

---

## 4. Capa Core

### 4.1 `NrrdHeader`

```csharp
public sealed class NrrdHeader
{
    public int[]      Sizes;            // [512,512,383] (X,Y,Z)
    public Vector3[]  SpaceDirections;  // 3 vectores fila, en mm (mundo LPS)
    public Vector3    SpaceOrigin;      // mm, LPS
    public string     Type;             // "short"
    public bool       LittleEndian;
    public string     Encoding;         // "gzip" | "raw" | "gz"
    public long       DataByteOffset;   // byte donde empiezan los datos (tras línea en blanco)
    public int        VoxelCount => Sizes[0]*Sizes[1]*Sizes[2];
}
```

### 4.2 `NrrdReader`

```csharp
public static class NrrdReader
{
    // Parsea el header ASCII. Lee el archivo en binario, separa header de datos por
    // la primera línea en blanco ("\n\n" o "\r\n\r\n"). NO descomprime acá.
    public static NrrdHeader ReadHeader(string path);

    // Lee + descomprime + convierte a short[] (índice lineal Fortran: X varía más
    // rápido, luego Y, luego Z → idx = x + y*sx + z*sx*sy).
    public static short[] ReadData(string path, NrrdHeader header);

    // Conveniencia: header + datos en una llamada.
    public static (NrrdHeader header, short[] data) Read(string path);
}
```

Detalles de implementación obligatorios:
- **Header**: ASCII. Primera línea debe empezar con `NRRD`. Cada línea siguiente es
  `clave: valor` (campo) o `clave:=valor` (key/value). Ignorar líneas que empiezan
  con `#`. El header termina en la **primera línea vacía**; los bytes que siguen son
  los datos. Guardar ese offset en `DataByteOffset`.
- Parsear `sizes` (ints separados por espacio), `space directions` (3 tuplas
  `(a,b,c)`), `space origin` (tupla), `endian`, `encoding`, `type`.
- **Datos**: si `encoding` es `gzip`/`gz` → `GZipStream` (Decompress) sobre el bloque
  de datos; si `raw` → leer directo. Resultado: `byte[]` de longitud
  `VoxelCount * 2`.
- **Endian**: el archivo es little-endian. En little-endian host, `short v =
  (short)(b[0] | (b[1] << 8))`. Convertir respetando `header.LittleEndian` (si fuese
  big, invertir). `BitConverter` está OK si se controla el orden.
- Validar: longitud descomprimida == `VoxelCount * 2`; lanzar excepción descriptiva
  si no.
- Memoria: ~200 MB de `short[]`. Aceptable como operación puntual.

### 4.3 `LabelVolume`

```csharp
public sealed class LabelVolume
{
    public int SizeX { get; }
    public int SizeY { get; }
    public int SizeZ { get; }

    // Afín IJK→LPS (mm). 3 columnas de dirección + origen.
    public Vector3 DirX { get; }   // dirección de +X de grilla, en mm/voxel (LPS)
    public Vector3 DirY { get; }
    public Vector3 DirZ { get; }
    public Vector3 OriginLps { get; }

    public LabelVolume(short[] data, int sx, int sy, int sz,
                       Vector3 dirX, Vector3 dirY, Vector3 dirZ, Vector3 originLps);

    // Construcción directa desde header + datos del reader.
    public static LabelVolume FromNrrd(NrrdHeader h, short[] data);

    // Etiqueta en (x,y,z). DEVUELVE 0 si está fuera de la grilla (clave para culling
    // en bordes). Acceso O(1).
    public short GetLabel(int x, int y, int z);
}
```

### 4.4 `VolumeResampler`

```csharp
public static class VolumeResampler
{
    // Remuestrea a voxel cúbico isotrópico de tamaño targetVoxelMm.
    // Calcula el factor por eje a partir del módulo de DirX/DirY/DirZ (spacing nativo):
    //   factorX = targetVoxelMm / |DirX|   (≈ 1.5/0.319 ≈ 4.7) etc.
    // La grilla de salida tiene tamaño ceil(sizeNativo / factor) por eje.
    // Para cada celda de salida, agrega las etiquetas de los voxels nativos que caen
    // dentro del bloque y aplica la regla:
    //   - Majority: etiqueta más frecuente; en empate gana la de id mayor
    //     (prioriza estructuras finas: spinal_cord/skin sobre hueso/grasa).
    //     El fondo (0) compite normalmente: si la mayoría del bloque es fondo, la
    //     celda queda 0.
    //   - AnyPresent: si alguna etiqueta !=0 aparece, toma la de mayor frecuencia
    //     no-cero (rellena más, útil para piel delgada).
    // La afín de salida se recalcula: DirX' = DirX_normalizado * targetVoxelMm, etc.,
    // mismo OriginLps.
    public static LabelVolume Resample(LabelVolume src, float targetVoxelMm,
                                       ResampleRule rule);
}
```

### 4.5 `CoordinateTransform`

Convierte coordenadas de grilla (resampleada) a posición Unity. **Esta es la parte
que evita el espejado.** Implementar exactamente este mapeo:

Direcciones anatómicas de los ejes de grilla (derivadas del signo de `space
directions` de este archivo):
- `+X` de grilla → patient **Right** (porque DirX.x < 0 y +X_LPS = Left)
- `+Y` de grilla → **Anterior** (DirY.y < 0, +Y_LPS = Posterior)
- `+Z` de grilla → **Superior**

Mapeo a Unity (Y-up, left-handed, Z-forward = anterior):
- `Unity.X = gridX`  (Right)
- `Unity.Y = gridZ`  (Superior → arriba)
- `Unity.Z = gridY`  (Anterior → adelante)

Es decir: **swap de los ejes Y/Z de grilla**. Este swap (permutación impar de un
sistema right-handed) produce un sistema left-handed → consistente con Unity, sin
mirror.

```csharp
public sealed class CoordinateTransform
{
    // metersScale = voxelSizeMm * unityUnitsPerMm  (lado del cubo en unidades Unity)
    public CoordinateTransform(float voxelSizeMm, float unityUnitsPerMm,
                               Vector3 gridOffset);   // gridOffset para centrar (en celdas)

    // Esquina de grilla entera (gx,gy,gz) → posición Unity.
    //   local = (gx,gy,gz) - gridOffset
    //   return new Vector3(local.x, local.z, local.y) * VoxelSizeUnity
    public Vector3 GridCornerToUnity(int gx, int gy, int gz);

    public float VoxelSizeUnity { get; } // voxelSizeMm * unityUnitsPerMm

    // Si el winding directo sale invertido por el swap de ejes, exponer un flag para
    // que VoxelMeshBuilder invierta el orden de triángulos. Determinar en pruebas:
    // por defecto FlipWinding = true (el swap Y/Z invierte la orientación de cara).
    public bool FlipWinding { get; } // = true
}
```

`gridOffset` lo calcula `SpineModelBuilder` si `centerModel`: el centro de la grilla
resampleada `(sizeX/2, sizeY/2, sizeZ/2)`, igual para las 13 mallas (así quedan
alineadas entre sí).

### 4.6 `VoxelMeshBuilder`

Genera **una malla** para una etiqueta con face-culling + greedy meshing.

```csharp
public static class VoxelMeshBuilder
{
    // Construye la malla de la estructura `labelId` dentro de `volume`.
    // - Ocupación booleana: occ(x,y,z) = (volume.GetLabel(x,y,z) == labelId).
    // - Una cara se emite entre una celda ocupada y un vecino NO ocupado, donde
    //   "no ocupado" = fondo, otra etiqueta, o fuera de grilla (GetLabel devuelve 0
    //   fuera de rango → cubre el borde automáticamente).
    // - Greedy meshing por las 6 direcciones de cara (±X,±Y,±Z): fusionar caras
    //   coplanares contiguas de la misma estructura en quads rectangulares máximos.
    // - Posicionar vértices con CoordinateTransform.GridCornerToUnity sobre las
    //   esquinas enteras de cada quad.
    // - Normales: por cara (planas, para preservar el look facetado). Recalcular o
    //   asignar a mano. Respetar transform.FlipWinding al ordenar los índices.
    // - Si el conteo de vértices supera 65.535, usar IndexFormat.UInt32 en la Mesh.
    // Devuelve null o malla vacía si la estructura no tiene voxels.
    public static Mesh Build(LabelVolume volume, int labelId,
                             CoordinateTransform transform, string meshName);
}
```

Algoritmo de greedy meshing (referencia, por cada uno de los 3 ejes `d` y cada signo
de cara `+/-`):
1. Sea `u,v` los otros dos ejes. Iterar las "capas" `w` a lo largo de `d`.
2. Para cada capa construir una máscara 2D `mask[u,v]`: `true` si la celda
   `(w en d)` está ocupada y su vecino en `w±1` (según el signo) NO lo está.
3. Recorrer la máscara: por cada celda `true` no consumida, extender un rectángulo
   máximo (primero en `u`, luego en `v` mientras toda la fila sea `true`), marcar
   consumidas, emitir un quad (2 triángulos) con las 4 esquinas en coordenadas de
   grilla y la normal = dirección de cara.
4. Las esquinas del quad son posiciones **enteras** de grilla → van directo a
   `GridCornerToUnity`.

Notas:
- Trabajar por bounding-box de la estructura (calcular min/max ocupado una vez) para
  no recorrer toda la grilla 13 veces.
- Vertex color opcional: no necesario, el color va por material (una malla = un
  material).

### 4.7 `SpineModelBuilder` (orquestador, sin Editor ni GameObject)

```csharp
public sealed class BuiltStructure
{
    public int   labelId;
    public Mesh  mesh;
    public StructureInfo info; // de la paleta
}

public static class SpineModelBuilder
{
    // Pipeline completo en memoria. NO crea GameObjects ni guarda assets.
    //   1. NrrdReader.Read(config.nrrdPath)
    //   2. LabelVolume.FromNrrd
    //   3. VolumeResampler.Resample(..., config.voxelSizeMm, config.resampleRule)
    //   4. gridOffset = centerModel ? centro de la grilla : Vector3.zero
    //   5. CoordinateTransform(config.voxelSizeMm, config.unityUnitsPerMm, gridOffset)
    //   6. Para cada labelId de config.palette.LabelIds: VoxelMeshBuilder.Build(...)
    //      (saltear mallas vacías). Nombre de malla = info.name.
    // `progress` opcional para reportar avance (0..1, mensaje) — usado por el baker.
    public static List<BuiltStructure> Build(
        BuildConfig config,
        Action<float, string> progress = null);
}
```

### 4.8 `MaterialFactory`

```csharp
public static class MaterialFactory
{
    // Crea un material URP/Lit. Si info.transparent → Surface Type = Transparent
    // (_Surface=1, blending alpha, render queue Transparent) y usa el alpha de
    // info.color. Si no, opaco. BaseColor = info.color.
    // Shader: Shader.Find("Universal Render Pipeline/Lit").
    public static Material Create(StructureInfo info);
}
```

---

## 5. Capa Editor — Plan A (bake one-shot, salida visible sin Play)

### 5.1 `SpineVoxelBaker`

```csharp
public static class SpineVoxelBaker
{
    [MenuItem("Tools/SpineVoxels/Build From Config")]
    public static void Build();   // usa el BuildConfig seleccionado en el proyecto
                                  // (o un campo fijo / Selection.activeObject)
}
```

Comportamiento:
1. Resolver el `BuildConfig` (pedirlo por selección o ruta fija configurable).
2. `EditorUtility.DisplayProgressBar` durante todo el proceso; cerrar en `finally`.
3. `SpineModelBuilder.Build(config, progress)`.
4. Crear carpeta de salida, p.ej. `Assets/SpineVoxels/Generated/`.
5. Por cada `BuiltStructure`:
   - `AssetDatabase.CreateAsset(mesh, ".../{name}.asset")`.
   - Material vía `MaterialFactory.Create` → `CreateAsset(material, ".../{name}.mat")`.
6. Construir jerarquía en escena: GameObject raíz `SpineModel` con un hijo por
   estructura (`MeshFilter` + `MeshRenderer` con la malla y el material). Aplicar
   `info.defaultVisible` a `GameObject.SetActive`.
7. Guardar la raíz como **prefab** en `Assets/SpineVoxels/Generated/SpineModel.prefab`.
8. `AssetDatabase.SaveAssets()` + `Refresh()`.

Resultado: el prefab/escena muestra el modelo en el editor **sin entrar a Play**.
Toggle de estructuras = activar/desactivar cada hijo en el inspector.

---

## 6. Capa Runtime — Plan B (fallback, solo si el bake es inviable)

### 6.1 `SpineModelRuntimeLoader : MonoBehaviour`

```csharp
public sealed class SpineModelRuntimeLoader : MonoBehaviour
{
    public BuildConfig config;
    public bool buildOnStart = true;

    // En Start(): parseo + resample + meshing en hilo de fondo (Task.Run); el
    // ensamblado de Mesh (SetVertices/SetTriangles/SetNormals) y la creación de
    // GameObjects DEBEN ocurrir en el main thread. Opción: generar arrays de
    // vértices/índices en background y subirlos a Mesh en una corrutina.
    // Mismo resultado que el baker pero solo visible en Play.
    public void BuildNow();
}
```

> Por qué es Plan B: el modelo no se ve en el editor. Solo usar si el bake en editor
> resulta demasiado pesado en RAM/tiempo al iterar.

---

## 7. Math de referencia (para tests)

Afín IJK→LPS de este archivo:
```
world_LPS(i,j,k) = OriginLps + i*DirX + j*DirY + k*DirZ
DirX = (-0.31919921875, 0, 0)
DirY = (0, -0.31919921875, 0)
DirZ = (0, 0, 0.6)
OriginLps = (64.9274, 233.0004, 1621.42)
```
Tamaño físico aprox del volumen: 512·0.319 ≈ 163 mm (X), 512·0.319 ≈ 163 mm (Y),
383·0.6 ≈ 230 mm (Z). Con `unityUnitsPerMm = 1` el modelo mide ~163×230×163 u.

Mapeo de grilla resampleada a Unity (sección 4.5): `(gx,gy,gz) → (gx, gz, gy)·lado`.

---

## 8. Casos borde y validaciones

- **Fondo (0) nunca se grafica**: no se crea malla para id 0; cuenta como vacío en el
  culling.
- **Bordes del volumen**: `GetLabel` fuera de rango devuelve 0 → las caras del borde
  se emiten → mallas cerradas.
- **Estructura ausente**: si una etiqueta no tiene voxels, `Build` devuelve malla
  vacía → el baker la saltea (sin GameObject).
- **>65k vértices**: usar `Mesh.indexFormat = IndexFormat.UInt32`.
- **Endianness**: respetar `header.LittleEndian`.
- **Orientación**: validar contra una captura conocida; si sale espejada, invertir un
  único eje en `CoordinateTransform` (no varios) y/o togglear `FlipWinding`.
- **Grasa/piel (12,13)**: `defaultVisible=false`, `transparent=true`.
- **Performance del bake**: aceptable como operación puntual (decenas de seg). Para
  iterar rápido, subir `voxelSizeMm` (p.ej. 3–4 mm) y bajarlo a 1.5 para el bake
  final.

---

## 9. Criterios de aceptación

1. El menú `Tools/SpineVoxels/Build From Config` genera mallas, materiales y un prefab
   `SpineModel` sin errores.
2. El prefab se ve en la escena **sin entrar a Play**, con look de cubos (facetado).
3. Las 13 estructuras (las presentes) son GameObjects separados, con su color, y se
   pueden activar/desactivar individualmente.
4. Grasa y piel arrancan ocultas.
5. Orientación anatómica correcta (no espejada): Superior hacia +Y, Anterior hacia +Z.
6. Ningún cubo de fondo renderizado.
