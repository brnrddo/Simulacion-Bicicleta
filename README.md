# Simulación Bicicleta

Simulador de ciclismo urbano en realidad virtual para investigación en transporte (Universidad de Chile). El participante pedalea una bicicleta real conectada por Arduino y recorre una ciudad con tráfico, peatones y semáforos, ambientada como Santiago.

## Requisitos

- **Unity 6000.3.11f1**, Built-in Render Pipeline.
- **Visor:** Meta Quest 3 por PC VR (OpenXR).
- **Bicicleta:** un sensor por Arduino (`SerialBikeInput`). Sin el sensor, se puede manejar con el teclado (`KeyboardBikeInput`).
- **Paquetes:** el Package Manager los descarga solo al abrir el proyecto (`Packages/manifest.json`). Uno de ellos es [osm2unity](https://github.com/rwth-irt/osm2unity), que genera la ciudad.

## Abrir el proyecto

1. Clona el repositorio y ábrelo con Unity Hub.
2. Abre la escena `Assets/EscenasLab/test.unity`.
3. Presiona Play.

La documentación del sistema de tráfico, peatones y ciclista está en `Documentacion/Sistema_de_trafico.md`. Las herramientas del Editor están en el menú `SimBici`.

## Contenido de terceros y créditos

El código y las herramientas propias tienen licencia MIT (ver `LICENSE`). El contenido de terceros conserva su propia licencia:

| Contenido | Autor | Licencia | Dónde |
|---|---|---|---|
| [Vintage Bicycle](https://sketchfab.com/3d-models/vintage-bicycle-1795f6e907634afda88fdaf45f67b279) (bicicleta del participante) | [VladNeko](https://sketchfab.com/vlad.neko) | [CC BY 4.0](http://creativecommons.org/licenses/by/4.0/) | `Assets/ThirdParty/Bike/VintageBicycle` |
| [Stoplight from Chile - Decayed](https://sketchfab.com/3d-models/stoplight-from-chile-decayed-5d41d46d0c3849e89de812f67c7de39f) (semáforos) | [Felipe Carvajal Brown](https://sketchfab.com/brownmarcostudios) | [CC BY 4.0](http://creativecommons.org/licenses/by/4.0/) | `Assets/ThirdParty/TrafficLights/StoplightChile` |
| Montreal Bus (bus rojo) | Nick Ladd | CC BY 3.0 | `Assets/ThirdParty/Vehicles/MontrealBus` |
| LowPoly Cars | [Quaternius](https://quaternius.com) | CC0 | `Assets/ThirdParty/Vehicles/QuaterniusCars` |
| Microsoft Rocketbox (peatones) | Microsoft | MIT | `Assets/ThirdParty/Rocketbox` |
| osm2unity (generación de la ciudad) | Institute of Automatic Control, RWTH Aachen | BSD 3-Clause | Paquete (`manifest.json`) |
| glTFast, XR Interaction Toolkit, Splines, AI Navigation y otros | Unity Technologies | Licencias de cada paquete | Paquetes |
| Meta XR SDK | Meta | Licencia del SDK | Paquete |

**Cambios sobre los modelos de terceros:**

- **Bicicleta:** escala, pivotes y separación en piezas (manubrio con horquilla y ruedas) para la animación; se quitaron los cables. Herramienta `SimBici > Build Player Bike Model`.
- **Semáforo:** escala, orientación, poste y caja en negro, y materiales propios de las luces. Herramienta `SimBici > Build Signal Models`.
- **Bus:** colores de Red Movilidad, sin logos.

La decoración de la ciudad (señales, placas, paraderos, veredas, cordillera) se genera por código, con `SimBici > Decorate City`.
