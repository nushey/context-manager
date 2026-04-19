# Context Manager MCP — Plan Definitivo

## Qué es

Un MCP server escrito en C# que usa Roslyn para extraer contexto estructural de proyectos C#. Cuando el agente necesita entender un archivo o un conjunto de archivos, llama a este MCP en vez de leer el código fuente crudo. Recibe de vuelta las firmas, dependencias, atributos y relaciones de tipos en JSON compacto y determinístico.

Todo en el mismo ecosistema: C# para el server, Roslyn para el análisis, NuGet para la distribución.

---

## Por qué C# nativo y no Python + tree-sitter

### Roslyn vs tree-sitter para análisis de C#

| | Roslyn | tree-sitter-c-sharp |
|---|---|---|
| **Quién lo mantiene** | Microsoft (es el compilador real) | Comunidad open source |
| **Cobertura de sintaxis** | 100% C# 13, siempre al día | Va atrás, features nuevos tardan |
| **Tipo de AST** | Nodos tipados (`ClassDeclarationSyntax`, `MethodDeclarationSyntax`) | Nodos genéricos, navegás por strings |
| **Semantic model** | Sí — resuelve tipos, interfaces, herencia a nivel de compilación | No — solo texto, sin resolución de tipos |
| **Dependencias** | Un NuGet package (`Microsoft.CodeAnalysis.CSharp`) | Binding Python + gramática C nativa + compilación |
| **Records, file-scoped namespaces, primary constructors** | Sí, nativo | Soporte parcial o tardío |
| **Distribución** | `dotnet tool install` o NuGet | `pip install` + bindings nativos |

**La diferencia que más importa:** Roslyn puede resolver que `IOrderService` está implementado por `OrderService` usando el semantic model. Tree-sitter solo ve el texto `IOrderService` como un string — no puede saber qué clase lo implementa sin heurísticas de nombre. Para `inspect_context`, donde las cross-references son el valor principal, esto es la diferencia entre 95% de precisión y 60%.

### Stack técnico

- **MCP SDK:** `ModelContextProtocol` v1.2.0 (NuGet, oficial, mantenido por Microsoft)
- **Análisis:** `Microsoft.CodeAnalysis.CSharp` (Roslyn)
- **Hosting:** `Microsoft.Extensions.Hosting` (stdio transport)
- **Target:** .NET 8 LTS (máxima compatibilidad)
- **Distribución:** NuGet como dotnet tool

---

## Scope

**Hace:**
- Extraer información estructural de archivos `.cs` dentro de un proyecto `.csproj`
- Devolver JSON determinístico — misma entrada, siempre misma salida
- Proveer dos niveles de extracción: archivo individual y contexto multi-archivo con cross-references
- Usar el semantic model de Roslyn para resolver tipos reales, no solo nombres

**No hace:**
- Analizar un `.sln` completo (eso es territorio de AGENTS.md / agents-md-generator)
- Cachear nada — Roslyn parsea un archivo en milisegundos, un proyecto de 500 archivos en menos de 3 segundos
- Llamar a ningún LLM o API externa — computación local pura
- Leer bodies de métodos — si el agente necesita la lógica, lee el archivo
- Resolver internals de paquetes NuGet — solo tu código fuente

**Por qué no cache:** Roslyn sin semantic model parsea ~10ms por archivo. Con semantic model (compilación de proyecto), ~2-3 segundos para 500 archivos. En una llamada on-demand esto es imperceptible. Cache agrega complejidad de invalidación sin beneficio real.

**Por qué no solución:** Una solución puede tener 20+ proyectos. El agente ya debería saber la estructura general (AGENTS.md). Este tool responde "contame sobre *este proyecto*" — no "explicame todo el monorepo."

---

## Tools: dos

### Tool 1: `inspect_file`

**Cuándo lo llama el agente:** Tiene un archivo específico y necesita entender su estructura antes de leer el source completo. O necesita el contrato estructural (firmas, deps) sin el ruido de implementación.

**Input:**
```json
{
  "filePath": "src/Services/OrderService.cs"
}
```

**Output:**
```json
{
  "file": "src/Services/OrderService.cs",
  "namespace": "MyApp.Services",
  "usings": ["MyApp.Domain", "MyApp.Repositories", "MediatR", "Microsoft.Extensions.Logging"],
  "types": [
    {
      "name": "OrderService",
      "kind": "class",
      "access": "public",
      "base": "BaseService",
      "implements": ["IOrderService"],
      "attributes": ["Scoped"],
      "constructorDependencies": [
        { "type": "IOrderRepository", "name": "orderRepository" },
        { "type": "IEventBus", "name": "eventBus" },
        { "type": "ILogger<OrderService>", "name": "logger" }
      ],
      "methods": [
        {
          "name": "CreateOrder",
          "access": "public",
          "returnType": "Task<OrderResult>",
          "parameters": [
            { "type": "CreateOrderDto", "name": "dto" },
            { "type": "CancellationToken", "name": "cancellationToken" }
          ],
          "attributes": []
        }
      ],
      "properties": []
    }
  ]
}
```

**Reglas de extracción — qué entra y qué no:**

| Elemento | ¿Incluir? | Por qué |
|---|---|---|
| Métodos `public` | Siempre | Superficie del contrato |
| Métodos `internal` | Siempre | Visibles dentro del proyecto |
| Métodos `private` | Nunca | Detalle de implementación |
| Métodos `protected` | Siempre | Relevantes para herencia |
| Constructor + params | Siempre, promovidos a `constructorDependencies` | Data de más alta señal en C# |
| Atributos en clase | Siempre, texto completo con argumentos | Wiring, routing, lifecycle |
| Atributos en métodos | Siempre | `[HttpGet("orders/{id}")]`, `[Authorize(Policy = "Admin")]` |
| Properties (en clases con métodos) | Solo nombre + tipo + acceso | Suficiente para entender la forma |
| Properties (en DTOs) | Omitir todas | Nombre + `"kind": "dto"` es suficiente |
| Base class | Siempre | Cadena de herencia |
| Interfaces implementadas | Siempre | Contratos de abstracción |
| Clases nested | Aplanar al nivel top del type list | Evitar nesting profundo en output |
| `using` statements | Siempre | Muestra dependencias y referencias de capa |
| XML doc comments | Nunca | Demasiado verbose |
| Bodies de métodos | Nunca | No es estructura, acá aplica "leé el archivo" |
| `public const` / `public static readonly` | Siempre | Valores tipo config que vale exponer |
| Enums | Nombre + miembros (solo nombres, sin valores numéricos) | Barato, útil para entender dominio |
| Records | Tratar como clase, extraer params del primary constructor | Patrón C# 9+ |
| Delegates/Events | Nombre + firma | Parte del contrato |

**Heurística de detección de DTO:**

Un tipo se clasifica como `"kind": "dto"` cuando cumple alguna de estas:
1. Tiene cero métodos (excluyendo compiler-generated)
2. No tiene constructor con parámetros Y todos sus members son auto-properties
3. Su nombre matchea patrones comunes: `*Dto`, `*Request`, `*Response`, `*Command`, `*Query`, `*Event`, `*Model`, `*ViewModel`

Cuando `kind` es `"dto"`, las properties se omiten. El agente puede inferir que `CreateOrderDto` tiene campos relacionados a orders. Si necesita la lista exacta, lee el archivo.

---

### Tool 2: `inspect_context`

**Cuándo lo llama el agente:** Va a trabajar en una feature que cruza múltiples archivos — implementar algo, trazar una cadena de dependencias, entender cómo un set de tipos se relaciona.

**Input:**
```json
{
  "filePaths": [
    "src/Controllers/OrderController.cs",
    "src/Services/OrderService.cs",
    "src/Services/IOrderService.cs",
    "src/Repositories/IOrderRepository.cs"
  ]
}
```

**Output:**
```json
{
  "files": [
    {
      "file": "src/Controllers/OrderController.cs",
      "namespace": "MyApp.Controllers",
      "types": [
        {
          "name": "OrderController",
          "kind": "class",
          "base": "ControllerBase",
          "implements": [],
          "attributes": ["ApiController", "Route(\"api/[controller]\")"],
          "constructorDependencies": [
            { "type": "IOrderService", "name": "orderService" }
          ],
          "methods": ["CreateOrder(CreateOrderDto): Task<IActionResult>", "GetById(Guid): Task<IActionResult>"]
        }
      ]
    }
  ],
  "references": [
    { "from": "OrderController", "to": "IOrderService", "via": "constructor", "resolvedFile": "src/Services/IOrderService.cs" },
    { "from": "OrderController", "to": "CreateOrderDto", "via": "parameter", "resolvedFile": null },
    { "from": "OrderService", "to": "IOrderRepository", "via": "constructor", "resolvedFile": "src/Repositories/IOrderRepository.cs" },
    { "from": "OrderService", "to": "IOrderService", "via": "implements", "resolvedFile": "src/Services/IOrderService.cs" }
  ],
  "unresolved": ["CreateOrderDto", "OrderResult", "Order", "BaseService"]
}
```

**Diferencias clave con `inspect_file`:**

- Métodos comprimidos a firma de una línea (string, no objetos) para mantener el payload chico
- Properties nunca incluidas — demasiado ruido a escala multi-archivo
- El mapa de **cross-references** es el valor real: muestra cómo los archivos provistos dependen entre sí
- La lista **`unresolved`** le dice al agente qué tipos referenciados no están en el set — señal para "quizás deberías inspeccionar estos archivos también"

**Resolución de cross-references con Roslyn:**

Acá es donde Roslyn cambia el juego vs tree-sitter. En vez de matchear nombres como strings:

1. Se crea una `CSharpCompilation` con todos los archivos del set
2. Se obtiene el `SemanticModel` por archivo
3. Para cada referencia de tipo (constructor params, return types, base types, implements), se usa `model.GetSymbolInfo()` o `model.GetTypeInfo()` para resolver el símbolo real
4. Si el símbolo resuelve a una declaración dentro del file set → se registra la referencia con su archivo fuente
5. Si no resuelve dentro del set → va a `unresolved`

Esto significa que incluso si alguien usa un `using alias` o un tipo genérico complejo, Roslyn lo resuelve correctamente. No es heurística — es el compilador real.

**Límite de archivos:** Máximo 15 por llamada. Si el agente necesita contexto más amplio, eso es `agents-md-generator`.

---

## Cuándo el agente NO necesita este tool

El AGENTS.md del proyecto debería dejar esto claro:

```markdown
## Context Manager MCP

Usá el MCP `context-manager` para entender la estructura de código C# antes de leer archivos:

- `inspect_file` — Para entender los tipos, métodos, dependencias y atributos de un archivo. Más eficiente que leer el source completo cuando solo necesitás la API surface.
- `inspect_context` — Para trabajar con múltiples archivos. Muestra cómo los tipos se relacionan por inyección, herencia y referencias.

Cuándo leer archivos directamente:
- Cuando necesitás la lógica de un método (cómo funciona algo, no qué existe)
- Para archivos de config/startup (appsettings.json, Program.cs)
- Para archivos no-C# (Razor, SQL, JSON, YAML)
- Cuando necesitás editar código (necesitás el source real)
```

---

## Nodos de Roslyn utilizados

Para claridad de implementación, estos son los tipos concretos de `Microsoft.CodeAnalysis.CSharp.Syntax` que los extractores recorren:

| Concepto C# | Tipo Roslyn | Propiedades usadas |
|---|---|---|
| Clase | `ClassDeclarationSyntax` | `.Identifier`, `.Members`, `.BaseList`, `.AttributeLists`, `.Modifiers` |
| Interface | `InterfaceDeclarationSyntax` | `.Identifier`, `.Members`, `.BaseList` |
| Record | `RecordDeclarationSyntax` | `.Identifier`, `.ParameterList` (primary constructor), `.Members` |
| Struct | `StructDeclarationSyntax` | `.Identifier`, `.Members`, `.BaseList` |
| Enum | `EnumDeclarationSyntax` | `.Identifier`, `.Members` |
| Método | `MethodDeclarationSyntax` | `.Identifier`, `.ReturnType`, `.ParameterList`, `.Modifiers`, `.AttributeLists` |
| Constructor | `ConstructorDeclarationSyntax` | `.ParameterList`, `.Modifiers` |
| Property | `PropertyDeclarationSyntax` | `.Identifier`, `.Type`, `.Modifiers` |
| Atributo | `AttributeListSyntax` → `AttributeSyntax` | `.Name`, `.ArgumentList` |
| Using | `UsingDirectiveSyntax` | `.Name` |
| Namespace | `NamespaceDeclarationSyntax` / `FileScopedNamespaceDeclarationSyntax` | `.Name` |
| Parámetro | `ParameterSyntax` | `.Type`, `.Identifier`, `.Default` |
| Delegate | `DelegateDeclarationSyntax` | `.Identifier`, `.ReturnType`, `.ParameterList` |
| Event | `EventDeclarationSyntax` / `EventFieldDeclarationSyntax` | `.Identifier`, `.Type` |

Todos accesibles con LINQ, pattern matching (`node is ClassDeclarationSyntax cls`), y el `CSharpSyntaxWalker` como visitor.

---

## Arquitectura

```
Agente llama inspect_file o inspect_context
    │
    ▼
MCP Server (Console App, stdio transport)
    │  - ModelContextProtocol SDK
    │  - Microsoft.Extensions.Hosting
    │
    ▼
Tool Handler recibe el request
    │
    ▼
CSharpFileAnalyzer / CrossReferenceResolver
    │  - Lee archivo(s) .cs del disco
    │  - CSharpSyntaxTree.ParseText() por archivo
    │  - (inspect_context) CSharpCompilation.Create() con todos los archivos
    │  - (inspect_context) SemanticModel por archivo para resolver tipos
    │
    ▼
Extractor camina el AST con SyntaxWalker
    │  - Aplica reglas de extracción (acceso, DTO detection, etc.)
    │  - Construye modelo de output (FileAnalysis, TypeInfo, etc.)
    │
    ▼
JSON serializado de vuelta al agente via MCP
```

**No hay archivos intermedios.** No hay `payload.json` en disco. Parse → extract → return. Stateless.

**Detección de project root:** Subir desde el path del archivo hasta encontrar un `.csproj`. Ese es el boundary del proyecto. Si no encuentra `.csproj`, usa el working directory. El tool nunca cruza boundaries — no persigue `<ProjectReference>`.

### Estructura de la solución

```
ContextManager/
├── src/
│   ├── ContextManager.Mcp/                 # MCP server (Console App)
│   │   ├── Program.cs                      # Host setup, stdio transport, tool registration
│   │   ├── Tools/
│   │   │   ├── InspectFileTool.cs          # [McpServerTool] inspect_file
│   │   │   └── InspectContextTool.cs       # [McpServerTool] inspect_context
│   │   └── ContextManager.Mcp.csproj
│   │
│   └── ContextManager.Analysis/            # Core analysis logic (Class Library)
│       ├── Analyzers/
│       │   ├── FileAnalyzer.cs             # Single-file Roslyn extraction
│       │   ├── ContextAnalyzer.cs          # Multi-file + cross-references
│       │   └── DtoDetector.cs              # DTO heuristic logic
│       ├── Extractors/
│       │   ├── TypeExtractor.cs            # CSharpSyntaxWalker: classes, interfaces, records, structs, enums
│       │   ├── MemberExtractor.cs          # Methods, constructors, properties, events, delegates
│       │   └── AttributeExtractor.cs       # Attribute parsing with arguments
│       ├── Models/
│       │   ├── FileAnalysis.cs             # Output model for inspect_file
│       │   ├── ContextAnalysis.cs          # Output model for inspect_context
│       │   ├── TypeInfo.cs                 # Type representation
│       │   ├── MethodInfo.cs               # Method representation
│       │   └── ReferenceInfo.cs            # Cross-reference representation
│       └── ContextManager.Analysis.csproj
│
├── tests/
│   ├── ContextManager.Analysis.Tests/      # Unit tests con fixtures de archivos .cs
│   └── ContextManager.Mcp.Tests/           # Integration tests del MCP server
│
└── ContextManager.sln
```

**NuGet packages necesarios:**

```xml
<!-- ContextManager.Analysis.csproj -->
<PackageReference Include="Microsoft.CodeAnalysis.CSharp" Version="4.*" />

<!-- ContextManager.Mcp.csproj -->
<PackageReference Include="ModelContextProtocol" Version="1.*" />
<PackageReference Include="Microsoft.Extensions.Hosting" Version="8.*" />
```

---

## Distribución

### Como dotnet tool (recomendado)

```bash
dotnet tool install -g context-manager
```

Configuración en el cliente MCP:

```json
{
  "mcpServers": {
    "context-manager": {
      "command": "context-manager",
      "args": []
    }
  }
}
```

O si se usa con Claude Code:

```bash
claude mcp add context-manager -- context-manager
```

### Como NuGet package tool

Se publica a nuget.org con `<PackAsTool>true</PackAsTool>` en el `.csproj` del proyecto MCP:

```xml
<PropertyGroup>
  <PackAsTool>true</PackAsTool>
  <ToolCommandName>context-manager</ToolCommandName>
  <PackageId>ContextManager.Mcp</PackageId>
</PropertyGroup>
```

---

## Implementación por fases

### Fase 1 — `inspect_file` (MVP)

1. Solution scaffold: `ContextManager.Mcp` + `ContextManager.Analysis`
2. `Program.cs` con MCP host setup (stdio, `WithToolsFromAssembly`)
3. `TypeExtractor` usando `CSharpSyntaxWalker`: clases, interfaces, records, structs, enums
4. `MemberExtractor`: métodos, constructores, properties
5. `AttributeExtractor`: atributos con argumentos
6. `DtoDetector`: heurística de detección
7. `FileAnalyzer` que orquesta todo para un solo archivo
8. `InspectFileTool` que conecta con MCP
9. Tests con fixtures `.cs` reales

**Resultado:** Un tool funcional, testeado, con output determinístico. Probalo en tus propios proyectos C# y validá la calidad del output antes de agregar el segundo tool.

### Fase 2 — `inspect_context`

10. `ContextAnalyzer`: extracción multi-archivo con detalle reducido
11. `CSharpCompilation.Create()` con los archivos del set
12. `SemanticModel` por archivo para resolver tipos
13. Cross-reference map: constructor deps, implements, base types, parámetros
14. Lista de `unresolved`
15. Límite de 15 archivos
16. `InspectContextTool` conectado a MCP
17. Tests de cross-reference con fixtures multi-archivo

### Fase 3 — Polish y distribución

18. Edge cases: partial classes (flag con `"partial": true`), generic constraints, nullable reference types
19. Records con primary constructors (C# 9+)
20. `required` properties (C# 11+)
21. Template de AGENTS.md para integración con agentes
22. Packaging como dotnet tool
23. Publicación en NuGet
24. README con ejemplos de output reales

---

## Complementariedad con agents-md-generator

| | agents-md-generator | Context Manager |
|---|---|---|
| Scope | Proyecto/solución completa | 1–15 archivos |
| Propósito | Generar un documento persistente | Query estructural on-demand |
| Cuándo se llama | Una vez, después en cambios | Cada vez que el agente necesita estructura |
| Output | Archivo Markdown (AGENTS.md) | JSON via MCP |
| Cache | Sí (SHA-256 incremental) | No |
| Motor de análisis | tree-sitter (Python) | Roslyn (C#) |
| Lenguajes | Python, C#, TS, JS, Go | C# only |
| Resolución de tipos | Heurística de nombres | Semantic model del compilador |
| Nivel de detalle | Optimizado para overview | Full por archivo |

El agente lee AGENTS.md para entender el proyecto. Llama a Context Manager para entender archivos específicos antes de trabajar en ellos.

---

## Preguntas abiertas

1. **¿Debería `inspect_context` aceptar nombres de tipos en vez de paths?** Ejemplo: `"types": ["OrderService", "IOrderRepository"]` — el tool busca los archivos, extrae y cross-referencia. Requiere un scan del directorio del proyecto, pero mejora dramáticamente la UX para el agente. Considerarlo para Fase 2.

2. **Partial classes.** C# permite dividir una clase en múltiples archivos. Si el agente pasa un archivo, ¿debería el tool automáticamente buscar y mergear las otras partes? Recomendación: no para MVP, flag con `"partial": true`.

3. **¿Semantic model siempre o solo en `inspect_context`?** Crear una `Compilation` para un solo archivo es overkill si solo necesitás el syntax tree. Para `inspect_file`, el AST puro (sin semantic model) alcanza para todo excepto resolver implementaciones. Recomendación: `inspect_file` usa solo syntax tree, `inspect_context` usa compilación + semantic model.
