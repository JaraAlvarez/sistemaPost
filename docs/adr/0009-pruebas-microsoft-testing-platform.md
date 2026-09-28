# ADR-0009 · xUnit v3 sobre Microsoft Testing Platform; reglas de arquitectura propias

- **Estado:** Aceptada · 2026-09-28

## Contexto
xUnit v3 (4.x) usa Microsoft Testing Platform (MTP), y el SDK de .NET 10 ya no permite ejecutarlo con el motor anterior (VSTest). La propuesta de la Fase 1 mencionaba ArchUnitNET para las pruebas de arquitectura.

## Decisión
- Pruebas con **xUnit v3 + Shouldly** en modo MTP (`"test": { "runner": "Microsoft.Testing.Platform" }` en `global.json`).
- Cobertura con **coverlet.MTP** (MIT). Se descartó `Microsoft.Testing.Extensions.CodeCoverage` por tener licencia propietaria.
- Las reglas de arquitectura se implementan con **reflexión sobre el grafo de ensamblados**, en lugar de ArchUnitNET. Las reglas actuales son de dependencias entre proyectos y de tipos públicos: la reflexión basta, no añade dependencias y permite **pruebas negativas** que demuestran que cada regla detecta violaciones. Si en el futuro se necesitan reglas a nivel de instrucción IL, se reevaluará ArchUnitNET.

## Consecuencias
- ✅ `dotnet test` y `build.ps1` funcionan con el SDK actual; los ejecutables de prueba son autónomos.
- ⚠️ Visual Studio necesita la versión 17.14 o superior para ver las pruebas MTP en el Explorador de pruebas.
