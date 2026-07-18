# TypeSharp Deep Validation — Round 2

**106 pruebas independientes · 38 archivos TypeSharp**

Segunda tanda de validación para **TypeSharp / TS.NET Runtime**. Está diseñada para encontrar errores semánticos y de aislamiento que no suelen aparecer en pruebas funcionales básicas.

## Ubicación

Copia esta carpeta dentro del repositorio:

```text
TypeSharp/
├── src/
└── samples/
    └── deep-validation/
```

## Ejecución

```bash
dotnet run --project samples/deep-validation/TypeSharp.DeepValidation.csproj
```

PowerShell:

```powershell
dotnet run --project .\samples\deep-validation\TypeSharp.DeepValidation.csproj
```

## Cobertura

### Semántica

- Precedencia y asociatividad.
- `for`, loops anidados y referencias adelantadas.
- Recursión mutua.
- Ternario.
- Asignaciones compuestas.
- Incremento prefijo y posfijo.
- Arrays e índices.
- `try/catch/finally`.
- Nullable y acceso opcional.
- Cortocircuito real de `&&` y `||`, verificado mediante efectos host.

### Tipos numéricos

- Comparaciones completas de `int64` y `uint64`.
- Aritmética `uint64` por encima de `Int64.MaxValue`.
- Operaciones bitwise de 64 bits.
- `float32`.
- `decimal` exacto, comparación y serialización.
- División por cero.

### Objetos y clases

- Instancias independientes.
- Orden de argumentos del constructor.
- Herencia, `super` y métodos heredados.
- Objetos estructurales anidados.
- Escritura de propiedades de interfaz.

### Módulos

- Grafo de importación de tres niveles.
- Alias de importación.
- Archivos con el mismo nombre en directorios distintos.
- Clases importadas.
- IDs canónicos.
- Rechazo de ciclos y módulos ausentes.

### Interop C#

- Todos los escalares principales.
- `Task<T>` y `ValueTask<T>`.
- Excepciones host.
- Restricción `[TsExport]`.
- Nombres ambiguos.
- Retención del tipo `int64` en funciones host dinámicas.

### Bytecode

- Verificación de módulos generados.
- Serialización/deserialización y ejecución posterior.
- Constantes `decimal`.
- Opcode desconocido.
- Operando truncado.
- Índice local inválido.
- Branch hacia mitad de instrucción.
- Funciones duplicadas.
- Magic inválido.

### Hot reload

- Swap de generación.
- Detección de fuente sin cambios.
- Fallo sintáctico y de tipos sin romper la activa.
- Reintento válido.
- Startup canary y canary personalizado.
- Rollback.
- Retención limitada.
- Leases de generaciones.
- Eventos.
- Reload de dependencias, módulos con imports y módulos dependientes de host.
- Migradores.

### Aislamiento

- 5,000 ejecuciones secuenciales.
- 256 ejecuciones paralelas.
- Dos runtimes con símbolos idénticos.
- Recuperación tras límite de recursión.
- Corte de loop infinito.
- Presupuesto lógico de memoria por ejecución.

## Salida

El runner continúa después de cada fallo. Devuelve una sección final:

```text
FAILURES TO RETURN:
- [Área] Prueba: error exacto
```

Devuelve la salida completa, especialmente esa sección. Un fallo puede señalar una capacidad no implementada; eso es intencional en esta ronda.
