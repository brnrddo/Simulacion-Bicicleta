# Sistema de tráfico del simulador de ciclismo urbano en VR

**Documento técnico para el paper.** Describe el modelo, la matemática, los parámetros, las decisiones de diseño, la validación y las referencias del tráfico vehicular, los semáforos y la red del ciclista.

- **Proyecto:** `C:\Lab\Simulacion` (Unity), laboratorio de Ingeniería en Transporte, Universidad de Chile.
- **Responsable del estudio:** Bernardo.
- **Plan de implementación:** `PLAN_TRAFICO.md` (fases 0 y T2–T8).
- **Estado:** fases 0, T2, T3, T4 y T6 terminadas (T5 se dejó para después, por decisión de Bernardo). El 2026-10-13 se corrigieron los problemas vistos en el visor (sección 20). Falta repetir la prueba en el visor. Ver la [sección 16](#16-estado-pendientes-y-limitaciones).
- **Convención:** este documento se actualiza en cada paso de implementación. Los valores marcados con **[VERIFICAR]** deben confirmarse antes de publicarse.

---

## Índice

1. [Propósito y contexto](#1-propósito-y-contexto)
2. [Plataforma técnica](#2-plataforma-técnica)
3. [Arquitectura general](#3-arquitectura-general)
4. [Red vial de la escena](#4-red-vial-de-la-escena)
5. [Modelo de seguimiento vehicular (IDM)](#5-modelo-de-seguimiento-vehicular-idm)
6. [Generación de demanda y nivel de tráfico](#6-generación-de-demanda-y-nivel-de-tráfico)
7. [Ruteo](#7-ruteo)
8. [Reproducibilidad](#8-reproducibilidad)
9. [Interacción auto–ciclista en el carril](#9-interacción-autociclista-en-el-carril)
10. [Semáforos](#10-semáforos)
11. [Intersecciones: giros y derecho de paso](#11-intersecciones-giros-y-derecho-de-paso)
12. [Red de caminos del ciclista](#12-red-de-caminos-del-ciclista)
13. [Adelantamiento al ciclista (T4)](#13-adelantamiento-al-ciclista-t4)
14. [Peatones y pasos peatonales (T6)](#14-peatones-y-pasos-peatonales-t6)
15. [Registro de datos](#15-registro-de-datos)
16. [Estado, pendientes y limitaciones](#16-estado-pendientes-y-limitaciones)
17. [Validación](#17-validación)
18. [Decisiones del equipo](#18-decisiones-del-equipo)
19. [Inventario de archivos](#19-inventario-de-archivos)
20. [Historial de cambios](#20-historial-de-cambios)
21. [Referencias](#21-referencias)

---

## 1. Propósito y contexto

El simulador es un entorno de ciclismo urbano en realidad virtual para investigar psicología del transporte. El participante pedalea una bicicleta real montada en un rodillo; la velocidad de la rueda (sensor Hall) y el ángulo del manubrio (potenciómetro) se leen por serial desde un Arduino Uno.

**Variables manipuladas:** densidad de tráfico, distancia lateral de adelantamiento de los autos y cercanía de los peatones.

**Variables dependientes:** conducta (velocidad, desplazamiento lateral, frenado, respeto del rojo), choques y near-misses, y estrés (ratings y, opcionalmente, fisiología).

El tráfico debe ser:

- **Realista.** Seguimiento vehicular, semáforos, giros y derecho de paso calibrables con literatura.
- **Reproducible.** Con la misma semilla y la misma conducta del ciclista, el tráfico es el mismo.
- **Medible.** Cada interacción relevante con el ciclista queda registrada.
- **Controlable por condición.** El nivel de tráfico se cambia con un solo parámetro.

---

## 2. Plataforma técnica

| Elemento | Valor |
|---|---|
| Motor | Unity 6000.3.11f1 |
| Pipeline de render | **Built-in Render Pipeline**. El plan original decía URP; se verificó que el proyecto no tiene URP asignado ni el paquete instalado |
| VR | Meta Quest 3 en modo PC VR (Quest Link / Air Link), Meta XR Core SDK + OpenXR |
| Paso fijo de simulación | Δt = 0.0111 s (90 Hz). Toda la simulación corre en `FixedUpdate` |
| Splines | `com.unity.splines` 2.8.4 |
| Navegación peatonal | `com.unity.ai.navigation` 2.0.15 (NavMesh) |
| Avatares | Microsoft Rocketbox (licencia MIT), subconjunto de 16 avatares adultos ([14.1](#141-avatares)) |
| Input | Input System nuevo |
| Tests | Unity Test Framework 1.6.0, modo EditMode |
| Código | Ensamblado `SimBici` (`Assets/scripts`), `SimBici.Editor` (herramientas de editor) y `SimBici.Tests.EditMode` (`Assets/Tests/EditMode`) |
| Restricciones del entorno | Burst desactivado (Smart App Control bloquea sus DLL). No se usa Burst ni Jobs |

**Capas (layers):** 6 `Ground`, 7 `player`, 8 `Vehicle` (BoxCollider no trigger + Rigidbody kinemático), 9 `Pedestrian`, 10 `StaticObstacle`. `Vehicle × Vehicle` no colisiona: la separación entre autos la garantiza el modelo, no la física.

---

## 3. Arquitectura general

El tráfico es una **micro-simulación en una red de carriles** (grafo dirigido de splines). La posición de cada auto es un escalar *S* a lo largo de su carril (un carril es un spline dibujado en el sentido de circulación). La física de Unity no mueve a los autos: sus poses se fijan desde la simulación con Rigidbody kinemático.

### 3.1 Paso sincronizado

`TrafficManager` ejecuta **un único paso sincronizado** por `FixedUpdate` (orden de ejecución 20, después de la bicicleta):

0. Avanzar los semáforos (`IntersectionController.Tick`) y el regulador de salida (`RingExitController.Tick`).
1. Proyectar al ciclista sobre los carriles.
2. Generar autos (spawners).
3. Ordenar los autos de cada carril por *S* (ordenamiento por inserción; las listas casi siempre ya están ordenadas, así que es O(n)).
4. **Fase 1:** todos los autos calculan su aceleración IDM con el estado actual, sin mover a nadie.
5. **Fase 2:** todos integran (integración balística) y resuelven los cambios de carril.
6. Eliminar los autos que salen de la red (vuelven a un *pool* por prefab).
7. Aplicar las poses visuales (`Rigidbody.MovePosition` / `MoveRotation`).

Separar el cálculo de aceleraciones (fase 1) de la integración (fase 2) es la forma correcta de integrar un modelo de seguimiento: ningún auto reacciona a un estado que ya avanzó en el mismo paso.

### 3.2 Componentes principales

| Componente | Rol |
|---|---|
| `TrafficLane` | Carril: spline, límite de velocidad, ancho, conexiones `nextLanes` con pesos. En conectores: movimiento (recto/derecha/izquierda) y puntos de conflicto |
| `TrafficVehicle` | Estado de un auto: carril, *S*, velocidad, aceleración, parámetros IDM, ruta, estado de derecho de paso |
| `TrafficSpawner` | Llegadas de autos en el inicio de un carril de entrada |
| `TrafficManager` | Motor: paso sincronizado, líder, semáforos, derecho de paso, precalentamiento |
| `IntersectionController` | Ciclo semafórico de una intersección |
| `StopLine` | Línea de detención (semáforo, PARE o CEDA EL PASO) y eventos del ciclista en la línea |
| `IntersectionLaneBuilder` | Generador de los carriles, conectores y conflictos de una intersección |
| `CyclistNetwork` / `CyclistPath` / `RiderRoute` | Red de caminos del ciclista y seguimiento del ciclista sobre ella |
| `RingExitController` | Regula la salida de autos para mantener estable la cantidad |
| `ExperimentLogger` / `EventBus` | Registro de datos |

---

## 4. Red vial de la escena

**Escena de trabajo:** `Assets/EscenasLab/test.unity`.

### 4.1 Topología

- **Circuito (anillo):** dos rectas E–O (z ≈ ±50) unidas por dos curvas (x ≈ ±150).
  - **Anillo exterior:** circula en sentido antihorario visto desde arriba: hacia el este por la recta sur y hacia el oeste por la recta norte. Carriles `Lane_Main_*`.
  - **Anillo interior:** sentido horario. Carriles `Lane_Main2_*`.
- **Calles transversales de sentido único:**
  - x = 4: hacia el norte (`Lane_North_01..03`). Entrada a la red desde el sur.
  - x = −60: hacia el sur (`Lane_South_01..03`). Entrada a la red desde el norte.
  - x = 60, al sur del circuito: hacia el sur (`Lane_Extra`). **Calle de salida regulada** ([6.4](#64-regulación-de-la-salida-ringexitcontroller)).

### 4.2 Intersecciones

| Nombre (prefijo) | Centro (x, z) | Control | Accesos con llegada |
|---|---|---|---|
| `Interseccion_NO` (I_NO) | (−60, 50) | Semáforo | N, E, O (el brazo S es solo de salida) |
| `Interseccion_SO` (I_SO) | (−60, −50) | Semáforo | N, E, O |
| `Cruce_S_Centro` (I_SC) | (4, −50) | Prioridad del circuito; **PARE** en el acceso S | O, E, S |
| `Cruce_N_Centro` (I_NC) | (4, 50) | Prioridad del circuito; **PARE** en el acceso S | O, E, S |
| `Cruce_SE` (I_SE) | (60, −50) | Prioridad (en T; el brazo S es solo de salida) | O, E |

*Acceso* = brazo por el que **llegan** los autos (Norte = vienen desde el norte).

### 4.3 Perfil transversal del circuito

La calzada pintada del circuito mide 12 m. Se adoptó este perfil:

| Franja | Ancho |
|---|---|
| Ciclovía | 2.4 m |
| Carril de autos | 3.6 m |
| Carril de autos (sentido contrario) | 3.6 m |
| Ciclovía | 2.4 m |

- **Eje de cada carril de autos:** a **4.2 m** de su solera derecha (2.4 + 1.8).
- **Camino del ciclista en la ciclovía:** a **1.2 m** de la solera (centro de la ciclovía).

Los carriles del circuito, dibujados a mano, estaban entre 4.0 y 4.6 m de la solera. Se alinearon con la herramienta `SimBici > Align Lanes To Curb`:

1. A lo largo de cada carril, cada 2 m, se mide con rayos verticales la distancia a la solera derecha. Un punto está sobre la calzada si un rayo hacia abajo golpea un collider de la capa `Ground` que no es el terreno.
2. Se desplaza cada punto lateralmente hasta la distancia objetivo y se reconstruye el spline con tangentes suavizadas.
3. Se filtran los valores con una mediana móvil de 5 muestras y se interpolan los huecos.

Las calles transversales conservan la distancia que tienen en sus tramos rectos; solo se corrigen sus curvas.

**Suavizado (`SimBici > Smooth Lanes`, 2026-10-13).** La medición anterior es escalonada: se mide cada 2 m en pasos de 5 cm, y cada muestra quedaba como un nudo del spline. El rumbo de los carriles del anillo oscilaba 26–34 veces por carril, con curvaturas locales de hasta 4.8°/m. Como el auto se orienta con la tangente del carril, se veía serpentear en su propio carril.

Ahora cada carril se suaviza en planta:

1. Se muestrea cada 0.5 m y se calcula el rumbo θ(*s*) de cada segmento.
2. Se filtra θ con un núcleo gaussiano, con σ = 4 m y reflexión par en los extremos. Suavizar el rumbo, y no la posición, conserva el giro total y el radio de las curvas, y crea transiciones de curvatura suaves (tipo clotoide).
3. Cerca de cada extremo se corrige el rumbo hacia el original con una rampa de 3 m, para empalmar sin quiebre con los conectores.
4. Se reintegran las posiciones desde el inicio y el error de cierre se reparte linealmente, así el final coincide con el original.
5. Se reconstruye el spline con nudos cada 3 m.

Lo mismo se aplica a la red del ciclista, en su generador.

**Resultado:** 0–1 cambios de signo de curvatura por carril (antes 26–34) y curvatura máxima de 1.1–1.4°/m. El desplazamiento máximo es de 0.15 m y los extremos quedan exactos. En Play, la orientación de los autos cambió de signo 22 veces en 9.2 km recorridos, casi siempre en los empalmes con las intersecciones.

**Resultado:** la distancia lateral entre el extremo del manubrio y el costado de un auto que circula por el centro de su carril quedó en **1.76–1.82 m en todo el anillo**. Antes iba de 0.98 a 2.09 m.

**Medición posterior** (`SimBici > Analyze Lane Room`, rayos cada 0.05 m): la calzada mide **12.55 m**. Los ejes de los dos carriles de autos quedan a **4.16 m** entre sí y a 8.35 m de la solera izquierda. Es decir, el perfil efectivo tiene ~0.55 m más entre los carriles que el perfil nominal de 12 m.

---

## 5. Modelo de seguimiento vehicular (IDM)

Cada auto sigue al obstáculo más cercano adelante en su ruta (otro auto, el ciclista, una línea de detención o un punto de conflicto) con el **Intelligent Driver Model** (Treiber, Hennecke y Helbing, 2000):

$$
a_{IDM} = a\left[1 - \left(\frac{v}{v_0}\right)^{\delta} - \left(\frac{s^*(v,\Delta v)}{s}\right)^2\right]
$$

$$
s^*(v,\Delta v) = s_0 + \max\left(0,\; vT + \frac{v\,\Delta v}{2\sqrt{ab}}\right)
$$

| Símbolo | Significado |
|---|---|
| *v* | velocidad propia |
| *v₀* | velocidad deseada |
| *s* | distancia libre (gap) al obstáculo |
| Δ*v* = *v* − *v_líder* | velocidad de acercamiento |
| *T* | tiempo de separación |
| *s₀* | distancia mínima detenido |
| *a* | aceleración máxima |
| *b* | desaceleración cómoda |
| δ | exponente |

- **Sin obstáculo** (*s* = ∞), el término de interacción es 0.
- **Tope físico de frenado:** la aceleración se acota a −9 m/s² (`MaxBrakeMps2`).
- **Velocidad deseada:** *v₀* = límite del carril × factor del conductor. El límite por defecto de las calles es 50 km/h; el de los conectores se calcula ([11.2](#112-velocidad-en-curva)).

### 5.1 Integración balística

Se usa la integración balística recomendada para el IDM con pasos finitos (Treiber y Kanagaraj, 2015):

$$
v_{t+\Delta t} = v_t + a\,\Delta t,\qquad
\Delta x = v_t\,\Delta t + \tfrac{1}{2}a\,\Delta t^2
$$

Si *v*ₜ₊Δₜ < 0, el auto se detiene dentro del paso: Δ*x* = −*v*ₜ²/(2*a*) y *v* = 0. Nunca hay velocidad negativa.

### 5.2 Heterogeneidad de conductores (`DriverProfile`)

Cada auto sortea sus parámetros al aparecer, de normales truncadas (recortadas al rango) y con la semilla del experimento.

| Parámetro | Media | DE | Rango | Nota |
|---|---|---|---|---|
| Factor de velocidad deseada | 1.00 | 0.08 | 0.8–1.2 | *v₀* = límite × factor |
| *T* (s) | 1.3 | 0.2 | 0.8–2.0 | Punto de partida urbano; calibrar en piloto |
| *s₀* (m) | 2.0 | 0.3 | 1.2–3.0 | Ídem |
| *a* (m/s²) | 1.5 | 0.2 | 0.9–2.2 | Ídem |
| *b* (m/s²) | 2.0 | 0.3 | 1.3–3.0 | Ídem |
| δ | 4 | — | — | Treiber et al. (2000) |
| Brecha crítica (s) | 4.5 | 0.6 | 3.0–7.0 | [11.5](#115-aceptación-de-brecha) |
| Brecha extra desde PARE/CEDA (s) | +2.0 | — | — | [11.5](#115-aceptación-de-brecha) |

Normal estándar por Box–Muller con `System.Random`: *z* = √(−2 ln *u*₁) · cos(2π*u*₂), con *u*₁ ∈ (0, 1].

**Vehículo (`CarTest`):** largo 4.34 m y ancho 1.85 m, alineados con su BoxCollider.

---

## 6. Generación de demanda y nivel de tráfico

### 6.1 Llegadas

Cada `TrafficSpawner` genera autos al inicio de un carril de entrada. Los intervalos entre llegadas siguen una **exponencial desplazada**, que reproduce llegadas aleatorias tipo Poisson pero con un intervalo mínimo realista:

$$
h = h_{min} + \bigl(-\ln u\bigr)\,\bigl(\bar h - h_{min}\bigr),\qquad \bar h = \frac{3600}{q},\quad u\sim U(0,1]
$$

Aquí *q* es el flujo efectivo en veh/h y *h*min = 1.2 s. La media de *h* es 3600/*q*.

Un auto aparece con la cola en el inicio del carril y solo si hay espacio según el propio IDM: gap ≥ *s₀* + *vT*. La comprobación considera:

- el último auto del carril;
- el ciclista;
- los autos que vienen llegando desde el carril anterior.

Si no hay espacio, la llegada queda pendiente (máximo 5) y se descartan las que excedan ese máximo (se cuentan).

### 6.2 Entradas

No hay spawners en el anillo: el ciclista veía aparecer autos de la nada. Los autos entran solo por los bordes del mapa:

| Spawner | Carril | Flujo nominal | Acceso a la red |
|---|---|---|---|
| `Spawner_01` | `Lane_North_01` (x = 4, desde z = −128) | 400 veh/h | PARE en I_SC |
| `Spawner_02` | `Lane_South_01` (x = −60, desde z = 128) | 400 veh/h | Semáforo en I_NO |

### 6.3 Nivel de tráfico

El nivel de toda la escena se controla con un solo parámetro, `Flow Multiplier` (*m*), en `TrafficManager`:

- **Flujo efectivo de cada spawner:** *q* = *q*nominal × *m* × β, con β = 3 durante el precalentamiento y 1 en la sesión.
- **Cantidad objetivo de autos:** *N*\* = round(`vehiclesPerLevel` × *m*), con `vehiclesPerLevel` = 30. Nivel 1 → 30 autos; nivel 2 → 60.
- **Tope:** un spawner no genera mientras haya *N* ≥ *N*\* autos activos (la llegada queda pendiente).

**Precalentamiento:** antes de empezar se simulan 120 s de tráfico sin ciclista, para que la red no arranque vacía. Al terminar se eliminan los autos a menos de 20 m del ciclista. Ese tiempo es simulado, no real.

### 6.4 Regulación de la salida (`RingExitController`)

Para que la cantidad sea estable y el tráfico se renueve, una calle de salida (`Lane_Extra`, en I_SE) regula qué proporción *p* de los autos del anillo sale de la red. Se recalcula cada 1 s:

$$
p = \operatorname{clamp}\!\left(p_0 + K\,\frac{N - N^*}{N^*},\; p_{min},\; p_{max}\right)
$$

Con *p₀* = 0.30, *K* = 1.0, *p*min = 0.05 y *p*max = 0.80.

En los accesos del anillo a I_SE, el conector que sale recibe peso 100·*p*. Los demás movimientos se reparten 100·(1 − *p*) conservando su proporción original. Con más autos que el objetivo salen más; con menos, se quedan circulando.

### 6.5 Pesos de giro

| Accesos | Recto / derecha / izquierda | Objetivo |
|---|---|---|
| Calles que llegan desde afuera (I_SC S, I_NC S, I_NO N, I_SO N) | 20 / 40 / 40 | Propensos a entrar al anillo |
| Accesos del anillo en I_SC, I_NC, I_NO, I_SO | 90 / 5 / 5 | Los autos siguen circulando |
| Accesos del anillo en I_SE | Regulados por *p* | Salida |
| Por defecto en el builder | 70 / 20 / 10 | Valor del plan, confirmado por Bernardo |

---

## 7. Ruteo

Al entrar a un carril, el auto elige el siguiente al azar con probabilidad proporcional a los pesos de `nextLanes`.

**Elección con dos carriles de anticipación:** cada auto conoce su carril actual, el siguiente (`NextLane`) y el subsiguiente (`NextNextLane`). Es necesario porque los carriles de llegada a una intersección miden solo 1–3 m: con un solo paso de anticipación, el auto sabría qué giro hará cuando ya está en la línea, demasiado tarde para frenar.

El ruteo es estocástico pero determinista: usa un generador con semilla propio ([8](#8-reproducibilidad)).

---

## 8. Reproducibilidad

- **Una sola fuente de azar:** todo el azar de la simulación usa `System.Random`; nunca `UnityEngine.Random`.
- **Un generador independiente por consumidor**, creado con `TrafficManager.CreateRng(offset)`:

  $$\text{seed} = \text{masterSeed}\cdot 486187739 + \text{offset}\cdot 16777619\quad(\text{aritmética entera sin verificación de overflow})$$

  | Offset | Consumidor |
  |---|---|
  | 0 | Ruteo |
  | 1, 2, … | Spawners (cada uno distinto) |
  | 1000 | Retardo de arranque en verde |
  | 2000 | Adelantamiento (T4) |
  | 3000 | Peatones ambientales (T6) |
  | 3100, … | Eventos de peatones (cada uno distinto) |

  Agregar un spawner no altera la secuencia de los demás.
- **Orden estable:** los carriles, spawners, intersecciones y controladores se recorren ordenados por nombre; los autos, en orden de Id.
- **Nombres únicos:** todos los carriles tienen nombre único, porque el orden por nombre es parte del determinismo.
- **Tiempo:** toda la lógica avanza en `FixedUpdate` con `Time.fixedDeltaTime`. Los semáforos los avanza el mismo paso del tráfico, también durante el precalentamiento.
- **Resultado:** con la misma semilla, el mismo nivel y la misma conducta del ciclista, el tráfico es idéntico. Hay tests que verifican tiempos de llegada y conductores ([16](#17-validación)).

---

## 9. Interacción auto–ciclista en el carril

En cada paso se proyecta la posición del ciclista sobre cada carril. Se obtienen su posición *S*b, su desplazamiento lateral *l*b y su **velocidad a lo largo del carril**:

$$
v_{b,\parallel} = v_b\,(\hat f_b \cdot \hat f_{carril})
$$

Es negativa si el ciclista va en contra y cercana a 0 si cruza. Así, un auto frena antes frente a un ciclista que viene de frente que frente a uno que va en su mismo sentido.

**El ciclista cuenta como obstáculo** si |*l*b| ≤ ancho/2 + 0.3 m. En ese caso el auto lo sigue con el IDM, usando como velocidad del líder *v*b,∥ y como largo de la bici 1.8 m.

En T4 esta regla cambia según el modo de adelantamiento; ver [13.4](#134-cuándo-el-ciclista-es-obstáculo).

---

## 10. Semáforos

### 10.1 Ciclo

`IntersectionController` usa un ciclo de dos ejes (norte–sur y este–oeste) con todo-rojo:

> verde eje 1 → amarillo → todo-rojo → verde eje 2 → amarillo → todo-rojo

$$
C = G_{NS} + G_{EW} + 2(Y + R_{todo})
$$

El estado de un eje en el instante *t* se calcula de forma cerrada, sin temporizadores acumulados:

$$
t_c = (t + \text{offset} - t_{inicio\ verde\ del\ eje}) \bmod C
$$

- verde si *t*c < *G*;
- amarillo si *t*c < *G* + *Y*;
- rojo en el resto.

El rojo de un eje queda implícito por el ciclo del otro. Se expone también el tiempo en la fase y el tiempo restante.

**Valores por defecto** (confirmados por Bernardo): verde 25 s por eje, amarillo 3 s, todo-rojo 2 s → ciclo de 60 s. Desfase 0 en ambas intersecciones.

- El amarillo de 3 s es la práctica habitual en Chile. **[VERIFICAR]** el valor normativo en el *Manual de Señalización de Tránsito* del MTT, capítulo 4 (Semáforos).

### 10.2 Fases peatonales

El cruce peatonal que atraviesa un brazo es paralelo al eje perpendicular a ese brazo. Su fase es:

- **Caminar:** durante el verde vehicular paralelo, salvo los últimos *t*desp = 5 s.
- **Parpadeo:** los últimos *t*desp del verde paralelo.
- **No caminar:** el resto del ciclo.

### 10.3 Conducta de los autos en la línea de detención

La línea de detención es, por convención, el **final del carril de llegada**.

- **Rojo:** la línea es un obstáculo detenido de largo 0. El IDM se detiene a *s*₀ (~2 m) de un obstáculo, así que el obstáculo se ubica *s*₀ − 0.5 m más allá de la línea: el frente del auto queda a **0.5 m** de la línea pintada y no 2 m atrás (**[VERIFICAR]** con observación). Lo mismo vale para las esperas en la línea por PARE, bloqueo o cesión. La regla del amarillo usa la distancia real a la línea.
- **Amarillo:** se aplica una regla de decisión, calculada **una sola vez** por auto y acceso (el resultado se guarda para que no oscile). El auto se detiene si

  $$\frac{v^2}{2\,b_{safe}} \le d_{línea}$$

  con *b*safe = 4 m/s². Si no puede detenerse cómodamente, sigue, y esa decisión se mantiene aunque cambie a rojo antes de cruzar.
- **Retardo de arranque (opcional, apagado por defecto):** al pasar a verde, el primer auto detenido espera un tiempo sorteado de N(1.0, 0.3) s, con semilla.

### 10.4 Implementación visual

Las luces (`ResearchTrafficLight`, modelo `tl_simple_01` del asset *Traffic Lights System*) son solo visuales: muestran la fase que les asigna el controlador.

Como no hay bloom (Built-in RP sin post-procesado), cada lente encendida muestra además un **halo**: un quad aditivo de 0.8 m con degradado radial, para que las luces se vean en VR a 50–80 m. **[VERIFICAR]** su visibilidad en el visor.

Los semáforos decorativos de la ciudad y las señales PARE/CEDA sobrantes se desactivaron. Se agregaron señales PARE en los accesos sur de I_SC e I_NC.

---

## 11. Intersecciones: giros y derecho de paso

### 11.1 Geometría generada (`IntersectionLaneBuilder`)

Por cada brazo, el builder genera:

- un carril de llegada **In**, desde el carril existente hasta la línea de detención;
- un carril de salida **Out**, desde el borde de la intersección hasta el carril existente;
- un **conector** por cada movimiento hacia otro brazo: recto, derecha o izquierda. No se generan vueltas en U.

Los extremos coinciden exactamente con los carriles existentes.

**Distancia de la línea de detención al centro.** Se mide desde la geometría real (`SimBici > Fit Intersections To Curbs`, 2026-10-13); ver [11.8](#118-ajuste-a-las-soleras-líneas-de-detención-pasos-y-postes).

- **I_NO e I_SO (con semáforo):** 13.3–15.4 m.
- **Accesos con PARE de I_NC e I_SC:** 10.9–11.6 m.
- **Brazos con prioridad:** 7.5–9 m, sin cambios.

**Conectores:** son curvas de Bézier cúbicas entre el final del carril de llegada y el inicio del de salida. Los manejadores son tangentes a ambos carriles, con largo *h* = *k*·*c* (*c* = cuerda): *k* = 1/3 para los rectos y 0.4 para los giros (aproxima un arco de cuarto de círculo).

**Clasificación del movimiento**, con los rumbos de llegada **h**ᵢₙ y de salida **h**ₒᵤₜ:

- **h**ᵢₙ·**h**ₒᵤₜ > 0.7 → recto;
- (**h**ᵢₙ × **h**ₒᵤₜ)ᵧ > 0 → derecha;
- en otro caso → izquierda.

**Línea pintada.** En los accesos con semáforo, PARE o CEDA se pinta una franja blanca de 0.4 m (**[VERIFICAR]** con el Manual de Señalización de Tránsito). Va a lo ancho de los carriles de llegada y, en una calle de sentido único, de solera a solera.

### 11.2 Velocidad en curva

Límite de cada conector:

$$
v_{lim} = \operatorname{clamp}\left(\sqrt{a_{lat}\,R_{min}},\; 10\ \text{km/h},\; v_{calle}\right)
$$

Con *a*lat = 2.0 m/s² (punto de partida del plan). *R*min = 1/κmax, donde la curvatura κ del spline se muestrea en 51 puntos.

**Resultado:** radios de 5–7 m → límites de **12–14 km/h** en los giros.

### 11.3 Anticipación

El auto frena antes de entrar a un tramo más lento. Para cada uno de los dos carriles siguientes de su ruta:

$$
v_{0,ef} = \min\left(v_0,\; \sqrt{v_{lim,sig}^2 + 2\,b\,d}\right)
$$

Aquí *d* es la distancia al inicio de ese carril y *b* la desaceleración cómoda del conductor.

### 11.4 Puntos de conflicto

Al generar la intersección se calculan los conflictos entre todos los pares de conectores que salen de carriles de llegada distintos:

1. Se discretiza cada conector como polilínea en planta, cada 0.25 m.
2. Se calcula la **zona de conflicto**: sobre cada trayectoria, el tramo cuyos puntos quedan a menos de *w*z = 2.3 m de la otra (dos medios anchos de auto, 2 × 0.925 m, más 0.45 m de margen). Para cada conector se guarda el **inicio** (*s*) y el **fin** (*s*fin) de la zona.

Esta definición sirve igual para los cruces (la zona empieza ~*w*z antes del cruce) y para las **convergencias**. Ahí las trayectorias se acercan gradualmente antes de unirse, así que el punto donde se tocan está demasiado adelante. Inicialmente se usaba ese punto y el monitor de T4 detectó un roce en una convergencia; con la zona desapareció.

Cada conflicto guarda, en cada conector, el inicio y el fin de la zona sobre ambas trayectorias (*s*propio, *s*propio,fin, *s*otro, *s*otro,fin) y un **modo**:

| Situación | Modo |
|---|---|
| Ejes distintos en una intersección con semáforo | Los dos en `OccupancyOnly`: el ciclo ya los separa, así que solo se espera a un auto que ya está dentro del cruce |
| Uno debe ceder (regla de abajo) | Ese conector en `Yield`; el otro en `OccupancyOnly` |
| Igual prioridad | Los dos en `OccupancyOnly` |

**Regla de prioridad.** El movimiento A cede ante B si:

- A viene de un acceso secundario (PARE o CEDA) y B de uno principal; o
- vienen de accesos de la misma clase y rango(A) > rango(B), con rango recto = 0, derecha = 1, izquierda = 2.

Es decir, la izquierda cede a todos y la derecha cede al recto.

**Resultado:** 42 puntos de conflicto en las 5 intersecciones.

### 11.5 Aceptación de brecha

Antes de entrar a un conector con conflictos en modo `Yield`, el auto (detenido o acercándose a la línea) calcula, para cada vehículo con prioridad que se dirige al punto de conflicto, el tiempo de llegada:

$$
t_o = \frac{d_o}{v_o}
$$

Aquí *d*o es la distancia del frente del otro auto al inicio de la zona, siguiendo su ruta (incluye autos que aún no eligieron conector pero vienen hacia el carril de llegada correspondiente; criterio conservador). Si *v*o < 0.5 m/s, cuenta como detenido: *t*o = ∞, o 0 si ya está encima del punto.

El auto entra si, para todos:

$$
t_o \ge t_c^{(conductor)} + \Delta t_{sec}
$$

- *t*c: brecha crítica del conductor ([5.2](#52-heterogeneidad-de-conductores-driverprofile)).
- Δ*t*sec = 2.0 s si sale de un acceso con PARE o CEDA; 0 en otro caso.

**Referencia:** el plan propone 4.5 s para el giro a la izquierda permisivo. El *Highway Capacity Manual* usa brechas críticas base del orden de 4 s para el giro a la izquierda desde la vía principal y de 6–7 s para las maniobras desde la secundaria. **[VERIFICAR]** valores y edición exactos antes de citarlos.

### 11.6 Reglas adicionales

**PARE.** Detención obligatoria antes de evaluar la brecha: *v* < 0.3 m/s a menos de 3 m de la línea.

**No bloquear la intersección.** No entra si en el carril de salida (y el siguiente) no hay espacio para su largo + *s₀*, salvo que esa cola esté avanzando a más de 2 m/s. Desde el 2026-10-13:

- Si hay un paso peatonal a la salida, el espacio debe quedar **pasado el paso**, y la excepción de la cola en movimiento no aplica: si la cola se detenía, el auto quedaba sobre el paso.
- Una **bici detenida** dentro del carril de salida ocupa espacio como un auto. Antes el auto entraba, quedaba detrás de ella en el conector y bloqueaba los demás movimientos; por ejemplo, un giro a la izquierda que le cedía el paso, con una cola detrás que llenaba el carril contrario.

**No quedar detenido sobre un paso por la cola.** Si el líder es un auto lento (< 1.5 m/s) y detenerse detrás de él dejaría al auto sobre un paso peatonal, se detiene antes del paso (motivo `paso_libre`).

**Autos que asoman desde el carril siguiente.** Un auto que acaba de entrar a un conector todavía tiene la cola sobre el carril de llegada. Ahora se considera líder de los que esperan en ese carril. Antes, al detenerse en la línea, el auto de atrás no revisaba los carriles siguientes y se montaba sobre esa cola.

**Compromiso.** Cuando el auto ya no puede detenerse cómodamente,

$$d \le \frac{v^2}{2 b_{safe}} + 1$$

o cuando está detenido en la línea y se le permite entrar, la decisión queda tomada y no se reevalúa.

**Zona de conflicto dentro de la intersección.** Para cada conflicto de su conector, un auto se detiene 1 m antes del inicio de la zona si otro auto que ya está dentro de su conector llega antes. Un auto cuenta como fuera de la zona cuando su parte trasera pasó *s*fin + 0.5 m:

$$
t_o < t_v - 0.3\ \text{s},\qquad t = d/\max(v, 1)
$$

En empate (diferencia menor a 0.3 s), cede el que tiene modo `Yield`; con igual modo, pasa el de menor Id. Esta regla es simétrica, así que dos autos nunca se esperan mutuamente. Evita los choques cuando dos autos ya decidieron entrar.

**Conectores hermanos.** Los conectores que salen del mismo carril de llegada se superponen al principio, así que cada auto considera como líderes a los autos de los conectores hermanos. Al iniciar se calcula, para cada par, la distancia desde el inicio hasta donde sus ejes se separan más de 2.5 m (más 4.5 m de margen; mínimo 6 m). Antes era un valor fijo de 6 m; con las líneas de detención más atrás, un recto y un giro comparten más tramo y había superposiciones.

**Antibloqueo.** Si un auto lleva más de 20 s detenido esperando por un conflicto, entra el de menor Id. Se registra `traffic_deadlock`. Desde el 2026-10-13 esa entrada forzada **se salta la cesión, pero no la ocupación**: no entra si otro auto está físicamente en la zona de conflicto. Antes la ignoraba, y dos autos de fases distintas retenidos dentro del cruce por peatones se superponían.

### 11.7 Cesión al ciclista en los giros

Al generar la red del ciclista se calculan los cruces entre cada conector de autos que gira y cada camino de bici que sigue derecho (`BikeConflictZone`). Se usa la misma definición de zona, con *w*z = 1.5 m (medio ancho del auto + medio manubrio + margen). Esto cubre:

- el *right hook*: el auto dobla a la derecha cruzando la ciclovía por la que la bici sigue recto;
- el giro a la izquierda frente a la bici que viene de frente.

El auto no entra mientras la bici llegue al cruce en menos de 4 s:

$$t_b = \frac{d_b}{v_b}$$

Aquí *d*b se mide sobre la red del ciclista, incluidos los tramos de llegada hasta 40 m atrás.

**Resultado:** 26 cruces giro–bici.

---

### 11.8 Ajuste a las soleras: líneas de detención, pasos y postes

**Problema.** Las líneas de detención estaban a 8.5–9 m del centro, pero la curva de las esquinas termina a 8.8–11.1 m. Esto causaba tres problemas:

- los pasos peatonales quedaban dentro de la esquina, más largos que la calle (11.5–15.3 m) y superpuestos entre sí;
- los autos se detenían dentro de la curva;
- 7 de los 12 postes de semáforo tenían la base en la calzada, entre 0.24 y 1.0 m fuera de la vereda (casi todos los del otro lado del cruce).

**Herramienta `SimBici > Fit Intersections To Curbs`.** Para cada brazo de las intersecciones con semáforo y cada brazo con PARE o CEDA:

1. **Medición sobre el NavMesh de veredas** (área «Vereda»), por búsqueda gruesa de 0.5 m y fina de 0.05 m:
   - las soleras derecha e izquierda en el tramo recto, como mediana entre 13 y 25 m del centro;
   - la distancia *d*esq a la que termina la curva de la esquina: desde ahí hacia afuera, la solera está a menos de 0.15 m de su valor recto, en ambos lados.
2. **Con semáforo:** paso peatonal en [*d*esq + 0.3, *d*esq + 0.3 + *w*], con *w* = 3 m. La línea de detención queda 1.2 m más afuera:

   $$d_{línea} = d_{esq} + 0.3 + w + 1.2$$

   **PARE/CEDA:** *d*línea = *d*esq + 0.5.
3. Si el carril existente que llega o sale queda a menos de 3 m de la línea, se recorta y se remuestrea.
4. Se regenera la intersección: carriles, conflictos, postes y pintura. Después se regeneran la red del ciclista, el espacio lateral y el setup de peatones.

**Postes.**
- **Del lado de llegada:** sobre la vereda derecha, 1 m antes de la línea.
- **Del otro lado del cruce:** pasado el paso peatonal del brazo de enfrente (borde exterior + 0.5 m), sobre su vereda. Antes iban a una distancia fija, que caía en la curva de la esquina.

**Resultado:**

| | Antes | Después |
|---|---|---|
| Línea de detención con semáforo | 8.5–9.0 m | 13.3–15.4 m |
| Línea de detención con PARE | 9.0 m | 10.9–11.6 m |
| Largo de los pasos peatonales | 11.5–15.3 m (cortaban la esquina) | 8.4–12.4 m (el ancho de la calle) |
| Postes con la base sobre la vereda | 5 de 12 | **12 de 12** |
| Distancia mínima de un giro a la vereda (eje del auto) | ≥ 4.0 m | 2.2–2.6 m en los giros a la derecha, sin invadir la esquina |

Hubo que recortar carriles de llegada y salida entre 3.4 y 8.3 m.

**Pendiente cosmético.** Las líneas segmentadas transversales que trae la textura de las calles de la ciudad no coinciden con los pasos nuevos: algunas quedan dentro del cruce o sobre un paso.

## 12. Red de caminos del ciclista

### 12.1 Generación (`SimBici > Generate Cyclist Network`)

Se deriva de la red de tráfico. Cada carril que no es conector da un tramo de bici en su mismo sentido:

- **Con ciclovía** (hay marcas de bicicleta pintadas a la derecha del carril): por el **centro de la ciclovía**, a 1.2 m de la solera, medido con rayos. Las marcas solo se usan para detectar que hay ciclovía.
- **Sin ciclovía** (calles de sentido único): **pegado a la solera**, con el centro del ciclista a 0.75 m de ella (`curbClearanceM`; queda ~0.45 m entre el extremo del manubrio y la solera). Por decisión de Bernardo: «como se anda en bici por la calle». Se usa la mediana por calle, sin los extremos, para que las esquinas no la deformen.
- **Tramos cortos** dentro de las intersecciones: heredan la distancia de la calle vecina.
- **Giros:** por cada conector de autos (recto, derecha o izquierda, cualquiera sea su peso para los autos), un tramo de bici con Bézier del mismo tipo.

**Resultado:** 46 tramos (30 por ciclovía, 16 junto a la solera), 32 giros y cruces, y 6 líneas de detención del ciclista (en los accesos con semáforo). Mayor desajuste entre tramos enlazados: 0.17 m.

### 12.2 Seguimiento del ciclista (`RiderRoute`)

El ciclista elige su camino; `RiderRoute` identifica en qué tramo va.

1. **Sin posición conocida:** busca en toda la red el tramo que mejor calza. Puntaje:

   $$|l| + 20\cdot[\text{va en contra y se mueve}] + 0.5\cdot[\text{es un giro}]$$

   Solo cuentan tramos a menos de 6 m.
2. **Al llegar al final del tramo:** pasa al sucesor con menor |*l*|.
3. **Dentro de una intersección**, todos los giros parten del mismo punto. Si un giro hermano calza al menos 0.5 m mejor que el elegido, cambia a ese.
4. **Fuera de la red:** si |*l*| > 4 m durante 45 ticks seguidos, vuelve a buscar en toda la red (evento `route_resync`).

**Variables publicadas:**

- `DistanceAlongM`: **odómetro**, los metros recorridos sobre la red desde el último inicio o reinicio.
- `LateralOffsetM`: desplazamiento lateral respecto del tramo, + = derecha. Es variable dependiente.
- `CurrentPath`: el tramo actual.

**Validación:** en una simulación de 1 100 m con dos giros a la izquierda y uno a la derecha se identificaron todos los tramos. El giro provisorio se corrige en pocos pasos y el odómetro nunca retrocede.

### 12.3 Eventos del ciclista en las líneas de detención

| Evento | Cuándo |
|---|---|
| `cyclist_wait_start` / `cyclist_wait_end` | Se detiene (< 0.3 m/s) a menos de 30 m antes de la línea. Incluye la duración de la espera |
| `cyclist_stopline_cross` | Cruza la línea. Incluye fase, tiempo en la fase, tiempo restante, velocidad y si se detuvo antes |
| `cyclist_intersection_exit` | Sale de la intersección. Incluye el **movimiento realmente hecho**, la fase al cruzar y el tiempo dentro de la intersección |

Al cruzar la línea todos los giros parten del mismo punto y aún no se sabe cuál tomará; por eso el movimiento va en `cyclist_intersection_exit` y no en `cyclist_stopline_cross`.

---

## 13. Adelantamiento al ciclista (T4)

**Decisión de Bernardo:** se aplica el **mismo modelo en todas las calles**, con o sin ciclovía (opción «a»).

### 13.1 Situación antes de T4

Los autos nunca se desplazaban lateralmente.

- **Bici por la ciclovía o junto a la solera:** el auto pasaba sin maniobra. La distancia manubrio–auto la fija la geometría:

  | Dónde va la bici | Distancia |
  |---|---|
  | Ciclovía del circuito | 1.76–1.82 m |
  | Calle x = 4 | ~2.0 m |
  | Calle x = −60 | ~2.3 m |
  | Calle x = 60 | ~2.5 m |

- **Bici dentro del carril:** el auto la seguía y no la adelantaba nunca.

### 13.2 Distancia lateral objetivo

Se sortea una vez por encuentro auto–ciclista, de una lognormal ajustada a Beck et al. (2019):

$$
D = \operatorname{clamp}\left(\tilde D\, e^{\sigma z},\ 0.5,\ 3.0\right)\ \text{m},\qquad z\sim N(0,1)
$$

$$
\tilde D = 1.73\ \text{m},\qquad \sigma = \frac{\ln(Q_3/Q_1)}{2\cdot 0.6745} = \frac{\ln(2.24/1.37)}{1.349} \approx 0.3645
$$

- La mediana, el rango intercuartil y los límites son configurables en `TrafficManager`, para manipularlos por condición.
- Con este recorte, aprox. 6.5 % de los sorteos queda en 3.0 m.
- La distancia se mide **desde el extremo del manubrio hasta el costado del auto**.
- **Medio ancho del manubrio:** *w*m/2 = 0.274 m (manubrio de 0.548 m medido en el modelo 3D de la bici). **[VERIFICAR]** con la bicicleta real.
- Generador propio: offset 2000 ([8](#8-reproducibilidad)).

### 13.3 Geometría y espacio disponible

Posiciones laterales respecto del eje del carril del auto, + = derecha. *l*b es la posición del ciclista y *w*a el ancho del auto (1.85 m).

**Posición lateral requerida del eje del auto** para pasar por la izquierda del ciclista a distancia *D*:

$$
L^* = l_b - \frac{w_m}{2} - D - \frac{w_a}{2}
$$

Si *L*\* ≥ 0, la geometría ya da la distancia y **no hay maniobra** (caso típico en la ciclovía).

**Espacio lateral** (`SimBici > Analyze Lane Room`): para cada carril se mide la distancia a la solera izquierda y se identifica el carril paralelo en sentido contrario (si lo hay). El auto usa, en orden:

| Maniobra | Límite de la posición del eje del auto | Condición |
|---|---|---|
| `in_lane` | \|*L*\*\| ≤ *e*/2 − *w*a/2 − 0.1 m (*e* = distancia entre ejes de carriles, 4.16 m en el anillo; ancho del carril en sentido único) | Ninguna |
| `one_way` | \|*L*\*\| ≤ *d*solera,izq − *w*a/2 − 0.3 m | Calle de sentido único |
| `opposing` | \|*L*\*\| ≤ *e* | Calle de doble sentido y brecha con el tráfico de frente |
| `partial` | Lo máximo que permite su carril o su calle | No hay brecha, pero la bici va **fuera** de la trayectoria del auto (ciclovía) |
| Seguir | — | No hay brecha y la bici va **dentro** del carril: la sigue con el IDM y reevalúa en cada paso |

**Brecha con el tráfico de frente.** Se exige

$$\mathrm{TTC}_{frente} \ge \max\!\left(5\ \text{s},\ t_{pasada} + 2\ \text{s}\right)$$

basado en Rasch et al. (2020), que observaron 5–7.5 s. La duración estimada de la pasada es

$$
t_{pasada} = \frac{g + L_a + L_b + 3}{\max(v_{0} - v_{b,\parallel},\ 1)} + \frac{T}{r(v)} + T
$$

*g* es la distancia al ciclista; *L*a y *L*b los largos del auto y la bici; *T* la duración de la maniobra lateral y *r*(*v*) su ritmo ([13.5](#135-maniobra-lateral)).

El tiempo a colisión es TTC = *d*/(*v* + *v*frente), donde *d* es la distancia longitudinal al primer auto que viene de frente (rumbo opuesto, a ±2.5 m del eje del carril contrario, hasta 200 m), medida en planta.

### 13.4 Cuándo el ciclista es obstáculo

La regla depende del modo y del estado de la maniobra. *c*(*L*) es la distancia lateral libre entre el costado del auto (en la posición *L*) y el extremo del manubrio.

**Modo `Disabled`:** la bici es obstáculo si va dentro del carril (|*l*b| ≤ ancho/2 + 0.3 m), como antes de T4.

**Modo `Free`:**

| Estado del auto | La bici es obstáculo si… |
|---|---|
| Sin maniobra | Va dentro del carril, o *c*(*L*) < 0.5 m |
| Desplazándose o desplazado | *c*(*L*llegada) < *D* − 0.05 m (con `partial`: < *c*(*L*destino) − 0.05 m). **No la pasa hasta tener la distancia planificada.** *L*llegada es la posición lateral que tendrá el auto al alcanzar la bici (ver abajo). Si la bici está detenida, basta *D* = 1 m |
| Volviendo al centro, con la bici todavía adelante | min(*c*(*L*), *c*(*L*destino)) < 0.5 m |

**Anticipación de la maniobra (2026-10-13).** Como la maniobra avanza con el avance del auto ([13.5](#135-maniobra-lateral)), evaluar la distancia lateral *actual* trababa la situación: un auto detenido detrás de la bici no podía correrse sin avanzar, ni avanzar sin haberse corrido. Pasaba, por ejemplo, después de un semáforo o de un reinicio tras choque. Ahora se evalúa la posición que tendrá el auto al llegar a la bici:

$$L_{llegada} = L_0 + \Delta y\, Q\!\left(x + \max\!\left(0,\ g - \frac{v^2}{2b}\right) \frac{dx}{ds}\right)$$

donde *g* es la distancia hasta la bici y *dx*/*ds* = ẋ/*v* (a baja velocidad, 1/*L*min). Solo cuenta el tramo que excede la distancia de frenado cómodo (*b* del conductor), así que si el plan no alcanza, el auto todavía puede detenerse. **Bici detenida en el carril (corregido el 2026-10-13).** En una calle de doble sentido los autos casi nunca la adelantaban y se quedaban atrás, sobre todo cerca de un semáforo. Había tres causas, todas del acople de ese día:

1. El tiempo de la maniobra para el TTC con el tráfico de frente se estimaba con la velocidad *actual*. Para un auto detenido daba ~50 s, así que nunca encontraba brecha. Ahora se usa la velocidad de salida, de al menos 3 m/s.
2. El auto seguía a la bici *t*follow antes de decidir y en ese tiempo quedaba pegado a ella, a *s*₀ ≈ 2 m, desde donde ya no puede correrse. Ahora, con la bici detenida, decide de inmediato.
3. Si tiene que esperar (por ejemplo, porque viene tráfico de frente), se detiene a la distancia que necesita para salir después, no a *s*₀:

$$g_{espera} = \min\left(\frac{1.875\,|\Delta y|}{\tan 30^\circ},\ 10\ \text{m}\right)$$

Un auto que de todos modos queda pegado a la bici detenida espera a que avance.

**Seguimiento previo:** si la bici va dentro del carril, el auto la sigue al menos *t*follow antes de adelantar. Ese tiempo se sortea por conductor de N(2.0, 1.0) s recortada a [0.5, 6] s. Además, su velocidad deseada debe superar a la de la bici en más de 1.5 m/s.

### 13.5 Maniobra lateral

**Perfil quíntico** (mínimo jerk) entre la posición inicial y la final:

$$
\frac{y - y_0}{\Delta y} = 10x^3 - 15x^4 + 6x^5,\qquad
|\ddot y|_{max} = \frac{5.7735\,\Delta y}{T^2} \le a_{lat,max} = 1\ \text{m/s}^2
$$

$$
T = \operatorname{clamp}\left(\sqrt{5.7735\,|\Delta y|/a_{lat,max}},\ 2,\ 4\right)\ \text{s}
$$

**Acople con el avance (2026-10-13).** Un auto no puede correrse de lado sin avanzar. Antes, detenido avanzaba la maniobra al 30 % del ritmo y se deslizaba de costado. Además el auto se orientaba siempre con la tangente del carril, así que al adelantar se trasladaba de lado sin girar.

Ahora la maniobra no puede superar un ángulo máximo con el carril. Ese ángulo depende de la velocidad, porque a vuelta de rueda se gira más: va de ψ = 30° detenido (`lowSpeedOvertakeHeadingDeg`) a ψ = 15° desde 5 m/s (`maxOvertakeHeadingDeg`), con variación lineal entre ambos. **[VERIFICAR]** los dos valores. Como la pendiente máxima del quíntico es 1.875·Δ*y*/*L*, la maniobra necesita al menos

$$L_{min} = \frac{1.875\,|\Delta y|}{\tan\psi_{max}}$$

metros de avance. El parámetro *x* avanza con

$$\dot x = \min\left(\frac{1}{T},\ \frac{v}{L_{min}}\right)$$

A velocidad normal manda la duración *T*; a baja velocidad la maniobra se alarga; detenido se congela.

**Orientación.** El auto apunta en la dirección en que se mueve: su rumbo es el del carril más ψ = atan(d*y*/d*s*), con

$$\frac{dy}{ds} = \Delta y\, Q'(x)\, \frac{\dot x}{v}$$

Con *v* → 0 el límite es Δ*y* *Q*′(*x*)/*L*min.

**Secuencia:**

1. **Desplazamiento** hasta *L*\*.
2. **Mantener** la posición mientras pasa al ciclista.
3. **Volver** al centro cuando su parte trasera pasó el frente de la bici por 3 m.

**Abortar (solo `opposing`).** El auto vuelve a su carril si el TTC con el auto de frente cae bajo 2.5 s, **pero solo si todavía alcanza a detenerse detrás de la bici**:

$$g > \frac{v^2}{2\cdot 4} + s_0$$

Si ya va a su lado, completa la pasada. Antes de esta regla, un auto que abortaba al lado del ciclista volvía y lo golpeaba. Los autos que vienen de frente ven al que adelanta por su carril como un obstáculo que se acerca: velocidad del líder = −*v*.

**Ámbito.** No se adelanta dentro de las intersecciones **ni a través de ellas**: si entre el auto y la bici hay un conector, el auto espera. La relación con el ciclista se busca en el carril actual y hasta tres carriles adelante, porque los carriles de una intersección son cortos. Si no se encuentra durante una maniobra, el auto no vuelve hasta haberlo pasado o estar a más de 40 m.

**Terminar antes de la intersección (2026-10-13).** Un adelantamiento que sale del carril (contrario, o lado izquierdo de un sentido único) solo empieza si alcanza a pasar a la bici, recorrer el margen y volver antes del próximo conector:

$$g + L_{auto} + \ell_b + 3 + \max\left(\frac{1.875\,|\Delta y|}{\tan\psi(v_p)},\ v_p T\right) \le d_{interseccion}$$

donde *v*p es la velocidad de pasada: al menos 4 m/s con la bici detenida, o 0.8·*v*₀ con la bici en marcha. Si el auto sigue recto y puede cruzar sin detenerse (prioridad, o semáforo en verde con tiempo de llegar + 2 s), se suman el conector y la salida: puede terminar de volver dentro del cruce. Antes, un auto quedaba detenido en el carril contrario esperando el semáforo y bloqueaba a los que venían de frente.

**TTC con el tráfico de frente (2026-10-13).**

- **Recorrido por el trazado:** se recorre el carril contrario por su trazado, contando los autos que están en él antes de la posición del auto y los que van a entrar a él (su carril siguiente o el subsiguiente es el contrario). La búsqueda en línea recta perdía autos en las curvas y los que salían de una intersección, y se produjo un choque de frente.
- **Tiempo de la maniobra:** la estimación usa la aceleración real desde la velocidad actual:

  $$t = \max\left(\frac{D}{v_{rel}},\ \frac{-v + \sqrt{v^2 + 2 a D}}{a},\ t_{desplazamiento}\right) + T_{vuelta}$$

  Antes suponía que pasaba a la velocidad deseada, abortaba al arrancar detenido y sumaba dos veces el tiempo de correrse.
- **Frenado del que adelanta:** el auto que adelanta por el carril contrario también frena ante cualquiera que venga de frente por su trayectoria (motivo `de_frente`), no solo el de frente ante él.

**Retroceder para salir.** Si un auto quedó pegado a una bici detenida (por ejemplo, porque el ciclista frenó justo delante) y hay espacio detrás, retrocede a 0.8 m/s hasta la distancia que necesita para salir.

**Después de abortar.** Un auto que abortó y quedó detenido con la bici detenida adelante puede volver a decidir desde donde está. Mientras vuelve no puede moverse de lado sin avanzar.

### 13.6 Medición (`OvertakeMonitor`)

`OvertakeMonitor` va en la bici. Una **pasada** empieza cuando un auto en el mismo sentido está detrás del ciclista (entre 2 y 25 m, a menos de 5 m lateral) y termina cuando su frente queda 2 m adelante.

Durante la pasada se mide en cada paso la **distancia mínima real**: desde los dos extremos del manubrio, ubicados en el centro del manubrio del modelo ± *w*m/2, hasta la superficie del collider del auto (`Collider.ClosestPoint`).

- Se descartan las pasadas que nunca estuvieron a menos de 5 m.
- Se descartan los encuentros en curso si la bici salta más de 3 m en un paso (reaparición tras un choque).

| Evento | Contenido |
|---|---|
| `overtake_start` | maneuver, planned_m, expected_m, target_lateral_m, bike_lateral_m, bike_in_lane, gap_ahead_m, car_speed_mps, bike_speed_mps, oncoming_ttc_s, duration_s, lane |
| `overtake_abort` | oncoming_ttc_s, lateral_m, lane |
| `overtake_pass` | source=ambient, vehicle_type, planned_m, **min_real_m**, car_speed_mps, rel_speed_mps (en el punto más cercano), bike_speed_mps, maneuver, oncoming_ttc_s, side, path |

### 13.7 Modo y parámetros

`AmbientOvertakingMode { Free, Disabled }` en `TrafficManager`. Con `Disabled`, los autos siguen a la bici si va dentro del carril y pasan sin desviarse si va por la ciclovía.

| Parámetro | Valor | Fuente / nota |
|---|---|---|
| Mediana de *D* | 1.73 m | Beck et al. (2019) |
| Rango intercuartil de *D* | 1.37–2.24 m | Beck et al. (2019) |
| Recorte de *D* | [0.5, 3.0] m | Plan |
| Medio manubrio | 0.274 m | Modelo 3D. **[VERIFICAR]** con la bici real |
| *a*lat,max | 1.0 m/s² | Plan |
| Duración de la maniobra lateral | 2–4 s | Plan |
| TTC mínimo con el de frente / margen | 5 s / 2 s | Rasch et al. (2020) |
| TTC para abortar | 2.5 s | Punto de partida |
| Diferencia de velocidad para adelantar | 1.5 m/s | Punto de partida |
| Margen para volver | 3 m | Punto de partida |
| *t*follow | N(2.0, 1.0) s, [0.5, 6] | `DriverProfile`. Punto de partida; calibrar en piloto |

---

## 14. Peatones y pasos peatonales (T6)

**Decisiones de Bernardo:** se usan avatares Microsoft Rocketbox (16); hay pasos con semáforo en todos los brazos de I_NO e I_SO (el paso cebra que había a mitad de la recta norte se quitó el 2026-10-13); la densidad peatonal es configurable.

### 14.1 Avatares

**Fuente:** Microsoft Rocketbox Avatar Library (Gonzalez-Franco et al., 2020; licencia MIT).

**Subconjunto descargado** con `git sparse-checkout`, ~1 GB:

- `Female_Adult_01..08` y `Male_Adult_01..08`: modelo (`Export/*.fbx`) y texturas de color, normales y opacidad del cuerpo y la cabeza;
- 4 animaciones de caminar (`*_walk_neutral_01/02`, carpeta `motextr_xy`). **No son en el lugar:** `Bip01` avanza 1.2–1.6 m por ciclo (ver la animación más abajo);
- 4 animaciones idle (`*_idle_neutral_01`, `*_idle_breathe_01`).

La copia fuente queda en `C:\Lab\Simulacion\ThirdPartySrc\Microsoft-Rocketbox` y lo usado en `Assets/ThirdParty/Rocketbox`, con licencia y `LEEME.txt`.

**Importación** (`RocketboxImportPostprocessor`):

- texturas a 1024 px (originales de 4096) y mapas de normales como NormalMap;
- rig Generic, con el esqueleto común `Bip01`;
- animaciones en bucle.

**Modelo:** una malla de ~8 700 triángulos con 80 huesos.

**Materiales:** Standard del Built-in RP. El pelo y las pestañas usan recorte por alfa (*cutout* 0.5).

**Prefab** (`Assets/AssetsLab/Pedestrians/Ped_*.prefab`):

- `PedestrianAgent`, CapsuleCollider (radio 0.25 m, alto 1.75 m) y Rigidbody kinemático en la capa `Pedestrian`, que el `CollisionDetector` del ciclista ya detecta;
- `Animator` con un controlador Idle/Walk por el parámetro `Speed` (4 variantes: F/M × caminar 01/02);
- `LODGroup` que oculta al peatón cuando ocupa menos del 1.5 % de la altura de pantalla. El modelo no trae niveles de detalle.

**Animación (corregida el 2026-10-13).** En los clips originales el hueso raíz `Bip01` avanza 1.2–1.6 m por ciclo y vuelve de golpe al repetirse. Sumado al avance que da el script, el peatón parecía avanzar y retroceder «con lag».

- **Por qué no se usó *root motion*:** el avatar Generic no tomó el nodo raíz (`hasRootMotion` = falso).
- **Copias en el lugar:** se generan copias de los 4 clips (`Animations/InPlace/*_inplace.anim`). A la posición horizontal (*x*, *z*) de `Bip01` y `MotionExtractionHelper` se le resta la deriva lineal (último − primero)·*t*/*T*; queda el vaivén natural de la marcha.
- **Velocidad propia de cada clip** (avance por ciclo / duración): 1.214, 1.322, 1.012 y 1.348 m/s. La reproducción va a `Animator.speed` = *v* / *v*clip, así los pies no patinan.
- **Verificación en Play:** desplazamiento máximo del hueso raíz respecto del peatón de 0.054 m (antes 1.63 m) y 0 saltos mayores a 0.15 m entre frames en ~150 000 muestras.

### 14.2 Red peatonal (NavMesh)

- **Áreas:** «Vereda» (área 3, costo 1) y «Cruce» (área 4, costo 1.5, para preferir las veredas). La calzada **no es caminable**, así que los peatones ambientales solo cruzan por los pasos peatonales.
- **Geometría:** se usan solo los objetos marcados con `NavMeshModifier`: las 49 veredas de la ciudad (2 359 m de largo útil, sin contar duplicados) y la superficie invisible de cada paso. La superficie del paso entra 0.8 m bajo cada vereda para conectarse.
- **Generación en dos pasadas:**
  1. Solo veredas.
  2. Se buscan los extremos de cada paso donde empieza la vereda en esa malla.
  3. Se generan veredas + pasos.

  Las esquinas de la ciudad son placas de asfalto con colisionador y las veredas no tienen colisionador, así que un rayo no distingue la calzada de la vereda.
- **Resultado:** 511 triángulos (con los pasos en su ubicación nueva), guardados en `Assets/EscenasLab/NavMesh_Peatones_test.asset`.
- **Manzanas aisladas:** hay manzanas que solo se comunican por los 9 pasos, porque la ciudad no tiene otros. Por eso los peatones eligen solo destinos a los que pueden llegar.

### 14.3 Pasos peatonales

`Crosswalk`: rectángulo sobre la calzada, de solera a solera (A → B), de ancho *w*.

**Con semáforo** (8: los 4 brazos de I_NO e I_SO):

- **Ubicación:** entre el fin de la curva de la esquina y la línea de detención, con 3 m de ancho y 1.2 m entre su borde y la línea ([11.8](#118-ajuste-a-las-soleras-líneas-de-detención-pasos-y-postes)). Miden 8.4–12.4 m de largo, el ancho de cada calle. Antes estaban dentro de la esquina, con 11.5–15.3 m.
- **Fase:** siguen la fase peatonal del controlador para el brazo que cruzan ([10.2](#102-fases-peatonales)), con despeje (verde intermitente) de 5 s.
- **Semáforo peatonal visual:** poste con caja y dos lentes con halo a cada lado. Muestra rojo fijo, verde fijo o verde intermitente a 2 Hz.

**Paso cebra:** había uno en x = −28 m sobre la recta norte. **Se quitó el 2026-10-13** a pedido de Bernardo, porque quedaba casi junto al semáforo de I_NO. Las reglas de paso cebra siguen implementadas por si se agrega uno en otro lugar.

**Pintura:** franjas de 0.5 m cada 1.0 m, paralelas al tránsito.

**Registro en los carriles:** al iniciar, cada paso calcula dónde lo cruza cada carril de autos (intersección del carril, muestreado cada 0.5 m, con el eje del paso) y se registra en ese carril con su distancia *s* y su coordenada *u* sobre el paso.

### 14.4 Peatones ambientales (`PedestrianManager`)

**Cantidad:**

$$N = \operatorname{round}\left(\rho \cdot \frac{L_{vereda}}{100}\right)$$

*ρ* = `pedestriansPer100m` (configurable por condición; 2 por defecto, lo que da 47 peatones) y *L*vereda = 2 359 m.

**Aparición:** al iniciar, en puntos al azar de las veredas, ponderados por el largo de cada vereda y a más de 25 m del ciclista. **Nunca aparecen ni desaparecen a la vista.**

**Velocidad de marcha** (Knoblauch et al., 1996), sorteada por peatón:

$$v \sim N(1.35,\ 0.20^2)\ \text{m/s, recortada a } [0.9,\ 1.8]$$

**Destinos:** un punto de vereda al azar entre 30 y 200 m. El camino se calcula con `NavMesh.CalculatePath` (veredas y cruces). Al llegar, con probabilidad 0.3 se queda quieto entre 2 y 8 s; si no, elige otro destino.

**Movimiento: modelo de fuerza social (2026-10-13).** Antes era cinemático (seguir las esquinas a velocidad constante) y los peatones no se esquivaban entre sí. Ahora, en cada `FixedUpdate`, cada peatón tiene una velocidad **v** que cambia con una aceleración (Helbing & Molnár, 1995):

$$\mathbf a_i = \frac{v_i^0 \mathbf e_i - \mathbf v_i}{\tau} + \sum_j \mathbf f_{ij} + \mathbf f_{i,bici} + \sum_k \mathbf f_{i,auto}$$

- **Impulso:** hacia la esquina siguiente del camino, con τ = 0.5 s.
- **Repulsión entre peatones:** especificación «elíptica II» (Johansson, Helbing & Shukla, 2007), que **anticipa** con la velocidad relativa:

  $$\mathbf f_{ij} = A\, e^{(r_{ij} - b_{ij})/B}\, \frac{|\mathbf d| + |\mathbf d - \mathbf y|}{2 b_{ij}}\, \frac{1}{2}\left(\frac{\mathbf d}{|\mathbf d|} + \frac{\mathbf d - \mathbf y}{|\mathbf d - \mathbf y|}\right) w(\varphi)$$

  donde:
  - $\mathbf d = \mathbf x_i - \mathbf x_j$ y $\mathbf y = (\mathbf v_j - \mathbf v_i)\,\Delta t_a$;
  - $b_{ij} = \tfrac12\sqrt{(|\mathbf d| + |\mathbf d - \mathbf y|)^2 - |\mathbf y|^2}$, acotado a ≥ 0.05 m;
  - $r_{ij}$ es la suma de radios;
  - $w(\varphi) = \lambda + (1-\lambda)(1+\cos\varphi)/2$ es la anisotropía (lo que está adelante pesa más).

  Se agrega una componente tangencial hacia la derecha, κ|**f**|max(0, cos φ), para lo que viene de frente: con esa preferencia por esquivar a la derecha se evitan los bloqueos simétricos. Si un encuentro es exactamente de frente y en línea, los dos vectores unitarios se anulan y se usa la dirección directa.
- **Por qué la versión elíptica:** la circular original no impide que dos peatones que se encuentran de frente se superpongan a velocidad de marcha. Lo comprobé en simulación: con A = 2.1 m/s² y B = 0.3 m, la distancia mínima entre centros fue de 0.20 m.
- **Parámetros:** A = 3 m/s², B = 0.3 m, Δ*t*a = 1 s, λ = 0.3, κ = 0.3 y |**v**| ≤ 1.3 *v*⁰. Se eligieron por simulación: de frente, cruzándose y frente a un peatón detenido, la distancia mínima queda en 0.7–0.8 m y nadie se traba. **[VERIFICAR]** con una calibración publicada.
- **Ciclista:** se trata como otro agente, de radio 0.6 m y con su velocidad.
- **Autos:** junto al cruce, la repulsión se calcula desde el punto más cercano de su rectángulo.
- **Esperar o ceder:** al esperar el semáforo o ceder al ciclista o a un auto, la velocidad deseada es 0, pero el modelo igual los separa. Quienes esperan en la cuneta abren paso a los que vienen cruzando: el que cruza siente ×0.3 la repulsión de los que esperan, y el que espera siente ×2 la de los que cruzan.
- **Sobre lo caminable:** tras integrar, se lanza un rayo sobre el NavMesh desde la posición actual. Si cruza un borde, el peatón se detiene en él y se desliza a lo largo, quitando la componente de velocidad hacia el borde.
  - Ajustar al punto más cercano del NavMesh fallaba en la rampa del paso junto a la vereda: el punto más cercano en 3D quedaba atrás y el peatón retrocedía. Los cruces llegaron a tardar 74 s.
  - Fuera de su turno, el peatón no puede pisar la calzada de un paso.
- **Camino:** la esquina se da por alcanzada a 0.5 m. Si el peatón ya ve la esquina siguiente sin obstáculos en el NavMesh, salta la actual. Si queda trabado más de 4 s queriendo avanzar, recalcula el camino.
- **Determinismo:** no hay azar. Se recorre en el orden fijo de la lista, con una grilla espacial de 4 m.
- **Por qué no `NavMeshAgent`:** se sigue sin usar, porque su esquive no es determinista y corre en `Update`.

**Ceder al ciclista:** el peatón se detiene si tiene al ciclista a menos de 2.5 m adelante y a menos de 1 m de su línea. Los peatones scriptados no ceden.

**Estados** (para el registro de T7): walking, idle, waiting_signal, waiting_zebra, crossing, yielding_bike, yielding_car, scripted.

### 14.5 Reglas de cruce

**Peatón, paso con semáforo.** Empieza a cruzar solo con «caminar» y si alcanza a terminar antes del rojo peatonal:

$$t_{caminar,rest} + t_{despeje} \ge \frac{\ell_{cruce}}{v}$$

**Peatón, paso cebra.** La ley le da preferencia, pero no se lanza frente a un auto que no alcanza a frenar. Empieza si, para cada auto que se acerca por un carril que cruza el paso:

$$d \ge \frac{v_a^2}{2b} + s_0 \quad\lor\quad \frac{d}{v_a} > 4\ \text{s} \quad\lor\quad v_a < 0.3\ \text{m/s}$$

con *b* = 2.5 m/s². Un auto que ya está sobre el paso lo bloquea.

**Auto frente a un paso.** Se detiene con el frente 1.5 m antes del borde del paso (el obstáculo se corre *s*₀ − 0.5 m, como en las líneas de detención) si:

- hay un peatón sobre el paso a menos de (ancho del carril/2 + 2 m) de su franja; o
- hay un peatón sobre el paso **fuera** de esa franja que camina hacia ella y llegaría antes de que el auto termine de pasar, con 2 s de holgura:

  $$\frac{|u_p - u_c| - (w_c/2 + 1)}{v_p^{\to}} \le \frac{\max(d,0) + 1 + w + L_{auto}}{\max(v,1)} + 2$$

  donde *v*p→ es la componente de la velocidad del peatón hacia la franja;
- (solo paso cebra) hay alguien esperando en la cuneta y el auto puede detenerse cómodamente con su propia *b*.

**Bici atravesada en el carril.** Para saber si la bici está «en el carril», ahora se usa lo que ocupa a lo ancho del carril según su orientación:

$$a = \max\left(m_h,\ \frac{\ell_b}{2}\,|\sin\theta| + m_h\,|\cos\theta|\right)$$

con *m*h el medio ancho del manubrio, ℓb el largo de la bici y θ el ángulo entre la bici y el carril. Antes solo se miraba el punto del ciclista: con la bici atravesada (por ejemplo, detenida sobre un paso), la rueda delantera podía quedar ~0.9 m dentro del carril sin que el auto la viera. La distancia lateral de los adelantamientos usa el mismo *a*.

**Corrección del 2026-10-13 (atropello en el paso cebra).** Hubo tres fallas:

- un auto cuyo frente ya había pasado 0.5 m su punto de detención dejaba de mirar a los peatones del paso, y uno detenido «muy encima» arrancaba con el peatón todavía cruzando;
- solo se cedía a los peatones a menos de 2 m de la franja, sin mirar si iban hacia ella;
- el peatón calculaba si el auto alcanzaba a frenar hasta el borde del paso, no hasta donde el auto realmente se detiene.

Ahora:

- el auto evalúa el paso hasta que su frente sale por el otro borde y cede según el tiempo de llegada del peatón a su franja;
- el peatón exige que el auto pueda frenar 1.5 m antes del borde;
- el peatón que cruza **espera si un auto quedó en su camino** (estado `yielding_car`).

**Interbloqueo auto–peatón (corregido el mismo día).** Con la regla anterior, un auto que ya estaba sobre el paso esperaba a los peatones de su franja, y esos peatones lo esperaban a él: ninguno avanzaba. Ahora un auto que ya está sobre el paso solo se detiene por un peatón **justo delante de su frente** (hasta 3 m adelante y medio ancho + 0.6 m a cada lado). Los que quedan a su costado lo esperan, el auto termina de pasar y libera el paso.

**No quedar detenido sobre el paso.** Al entrar a una intersección, el auto exige espacio a la salida para quedar entero **pasado** el paso peatonal de salida (borde + largo + *s*₀), no solo su largo. Si no lo hay, espera en su línea (motivo «bloqueo»).

**Altura al cruzar.** El camino del NavMesh une las veredas (~0.2 m) en línea recta, y los peatones cruzaban a 0.225 m sobre el asfalto. Ahora, sobre la calzada del paso, caminan a la altura del asfalto, medida con un rayo al iniciar. En la vereda toman la altura del NavMesh.

En los pasos con semáforo esto hace que los autos que giran cedan a los peatones que cruzan con «caminar», y que nadie arranque con un peatón todavía en el paso.

**Peatón en la calzada fuera de un paso** (cruce scriptado). Se proyecta sobre los carriles cercanos (prefiltrados por la caja envolvente de cada carril) y cuenta como obstáculo detenido si está a menos de (ancho/2 + 0.8 m) del eje.

### 14.6 Eventos de peatones (manipulación experimental)

`PedestrianEvent` se ubica en un punto de la red del ciclista (tramo + *s*). Se dispara según el tiempo de llegada del ciclista a ese punto:

$$\mathrm{TTA} = d/v_b$$

donde *d* es la distancia sobre la red, incluidos los tramos de llegada hasta 200 m antes. **El peatón scriptado no esquiva al ciclista.** Hay dos eventos de ejemplo, desactivados, que se activan por condición.

**Pasada cercana (`NearPass`).** Con TTA ≤ 10 s aparece un peatón que camina de frente hacia el ciclista, paralelo a su camino. Su posición lateral respecto del eje del camino es

$$l_p = l_{ciclista} \pm \left(\frac{w_m}{2} + D_p + r_p\right)$$

con *D*p = 0.5, 1.0 o 2.0 m (zonas proxémicas) y *r*p = 0.25 m. Mientras está a más de 8 m del ciclista ajusta esa línea a la posición lateral actual del ciclista, a 0.6 m/s como máximo; en los últimos 8 m la mantiene fija.

**Cruce fuera del paso (`Jaywalk`).** Con TTA ≤ 15 s aparece un peatón esperando en la cuneta, a 3.5 m del camino del ciclista. Cuando el TTA cae a *t*\* ~ U(3, 4) s, sorteado con semilla, cruza en línea recta y perpendicular. Mientras está en la calzada, los autos lo ven ([14.5](#145-reglas-de-cruce)).

**Medición:** distancia mínima real desde los extremos del manubrio hasta la cápsula del peatón (distancia en planta − radio). No se mide mientras la bici está congelada por un reinicio tras choque.

| Evento | Contenido |
|---|---|
| `pedestrians_start` | count, per_100m, sidewalk_m, crosswalks |
| `pedestrian_event_start` | type, planned_m o t_target_s, tta_s, cyclist_speed_mps, pedestrian_speed_mps, side, path |
| `pedestrian_event_pass` | type, planned_m, **min_real_m**, cyclist_speed_at_min_mps, rel_speed_at_min_mps, path |

**Al terminar**, el peatón pasa a ser ambiental. Si quedó fuera de las veredas, desaparece cuando está a más de 25 m detrás del ciclista.

### 14.7 Parámetros

| Parámetro | Valor | Fuente / nota |
|---|---|---|
| Densidad *ρ* | 2 peatones / 100 m (configurable) | Por condición |
| Velocidad de marcha | N(1.35, 0.20) m/s, [0.9, 1.8] | Knoblauch et al. (1996) |
| Velocidad nominal de la animación | 1.3 m/s | Ajuste visual |
| Destinos | 30–200 m | — |
| Pausa | p = 0.3, 2–8 s | — |
| Despeje peatonal | 5 s | `IntersectionController`. **[VERIFICAR]** con la norma (largo del cruce / 1.2 m/s) |
| Brecha del peatón en el paso cebra | 4 s | Punto de partida |
| *b* para el paso cebra | 2.5 m/s² | Punto de partida |
| Ceder al ciclista | 2.5 m adelante, 1 m lateral | — |
| Pasada cercana *D*p | 0.5 / 1.0 / 2.0 m | Plan (zonas proxémicas) |
| Disparo del cruce fuera del paso | *t*\* ~ U(3, 4) s | Plan |

---

## 15. Registro de datos

`ExperimentLogger` escribe en `Documentos/SimBici/Logs/<participante>_<condición>_<fecha>`. Todos los números usan `CultureInfo.InvariantCulture` (punto decimal).

| Archivo | Contenido |
|---|---|
| `_meta.txt` | Participante, condición, escena, inicio UTC, versión de Unity, Δt, largo de la red del ciclista |
| `_continuous.csv` (90 Hz) | `t_session, t_unity, tick, pos_x, pos_y, pos_z, yaw_deg, speed_mps, steer_phys_deg, steer_eff_deg, yaw_rate_dps, route_dist_m, lateral_offset_m, off_route, input_source, route_path` |
| `_events.csv` | `t_session, t_unity, type, source_id, detail, rider_x, rider_z, route_dist_m, lateral_offset_m, route_path`. `detail` = `clave=valor;clave=valor` |

**Catálogo de eventos de tráfico:**

| Evento | Detalle |
|---|---|
| `traffic_start` | seed, flow_multiplier, target_vehicles, lanes, spawners, intersections, vehicles_after_warmup |
| `traffic_level` (cada 30 s) | vehicles, target, exit_share, flow_multiplier |
| `signal_phase` | approach, phase, time_to_change_s, cycle_s, t_signal_s, reason (start/change) |
| `traffic_deadlock` | lane, next, waited_s, reason, waiting_vehicles, resolution |
| `cyclist_wait_start` / `_end` | intersection, approach, phase, time_in_phase_s, time_to_change_s, speed_mps, dist_to_line_m, stopped_before, wait_s |
| `cyclist_stopline_cross` | Ídem |
| `cyclist_intersection_exit` | intersection, approach, movement, exit_path, phase_at_cross, time_in_intersection_s, speed_mps |
| `route_resync`, `off_route_start` / `_end`, `route_finished` | Seguimiento del ciclista |
| `overtake_start`, `overtake_abort`, `overtake_pass` | Ver [13.6](#136-medición-overtakemonitor) |
| `pedestrians_start`, `pedestrian_event_start`, `pedestrian_event_pass` | Ver [14.6](#146-eventos-de-peatones-manipulación-experimental) |

---

## 16. Estado, pendientes y limitaciones

| Fase | Estado |
|---|---|
| 0: auditoría y compilación de T1 | Terminada |
| T2: semáforos | Terminada |
| T3: giros y derecho de paso | Terminada |
| T4: adelantamiento ambiental | Terminada; falta la prueba en el visor |
| T5: adelantamientos scriptados | Pendiente (se dejó para después de T6, por decisión de Bernardo) |
| T6: peatones | Terminada; falta la prueba en el visor |
| T7: medición y logging de agentes | Pendiente |
| T8: integración con el experimento | Pendiente |

**Limitaciones conocidas:**

- **Capacidad de los PARE.** Un acceso con PARE de la calle x = 4 pasa del orden de **300 veh/h** con el tráfico actual del circuito, por la teoría de brechas. En niveles altos se forman colas ahí, aunque la cantidad total se mantiene en el objetivo.
- **Escena en binario.** `test.unity` sigue en serialización binaria aunque el proyecto está en *Force Text*; probablemente por las mallas y el terreno embebidos.
- **Pipeline de render.** El proyecto usa Built-in RP. Los halos de los semáforos son una solución propia.
- **Valores por verificar:** amarillo según la norma chilena, brechas críticas del HCM y ancho del manubrio real.
- **Tráfico de frente en las curvas.** El TTC con el auto de frente se mide en planta con la dirección del carril en la posición del auto, así que en curvas cerradas puede subestimar o perder un auto lejano. El aborto con margen y la reacción de los autos de frente cubren ese caso.
- **Distancia lateral fija en la maniobra.** El auto ambiental no corrige su posición si el ciclista se mueve lateralmente durante la pasada. La distancia real queda registrada igual (`min_real_m`). El ajuste continuo es para los adelantamientos scriptados (T5).
- **Bici detenida dentro del carril.** Un auto detenido justo detrás ya no puede correrse de lado (la maniobra está acoplada al avance), así que espera a que la bici avance. Si se detuvo con espacio adelante, se corre mientras avanza despacio, con un ángulo de hasta 15°.
- **Marcas de la ciudad.** Las líneas segmentadas transversales de la textura de las calles no coinciden con los pasos y las líneas nuevas.
- **Cadencia de un clip.** El clip `m_walk_neutral_01` es lento (1.01 m/s): para un peatón rápido (1.7 m/s) se reproduce a ×1.7.
- **Semáforo peatonal simple.** Usa lentes de color, sin el pictograma del peatón.
- **Pasada cercana a la vista.** El peatón aparece unos 10 s antes del encuentro (~13 m más allá del punto del evento) y podría verse aparecer si la vista está despejada.
- **Rendimiento con peatones.** Con 60 autos y 47 peatones, el Editor marca 12.1 ms por frame (6.0 ms del hilo principal), sobre el objetivo de 11.1 ms. Hay que medirlo en el visor.
- **Herramientas que dependen de otras.** `RingExitController` encuentra sus carriles por nombre. Orden al cambiar la geometría:
  1. `Align Lanes To Curb`, que incluye `Smooth Lanes`.
  2. `Fit Intersections To Curbs`, o `Generate Intersection Lanes`.
  3. `Generate Cyclist Network`.
  4. `Analyze Lane Room`.
  5. `Build Pedestrian Setup`, que rehace los pasos y el NavMesh y conserva la densidad elegida.
- **Tiempo del Editor.** Al entrar en Play, el precalentamiento bloquea el hilo principal unos segundos.

---

## 17. Validación

### 17.1 Tests automáticos (EditMode): 49 de 49 pasan

| Grupo | Tests |
|---|---|
| IDM | Equilibrio *s₀* + *vT* con aceleración ≈ 0; estabilidad del equilibrio; camino libre a *v₀*; frenado de emergencia acotado a 9 m/s² |
| Integración balística | Detención dentro del paso (avance *v*²/(2\|*a*\|)); nunca velocidad ni avance negativos (10 000 casos aleatorios); auto detenido sigue detenido |
| Determinismo | Misma semilla → mismos tiempos de llegada; offset distinto → secuencia distinta; intervalos ≥ mínimo y media 3600/*q* (±3 %, N = 20 000); misma semilla → mismos conductores |
| Semáforos | Largo del ciclo; secuencia y duraciones; nunca dos ejes con verde o amarillo a la vez; tiempo en fase y restante; desfase; eje inicial; fases peatonales |
| Amarillo | Se detiene si *v*²/(2*b*) ≤ *d*, con el borde exacto (14 m/s, 24.5 m); casos detenido y ya cruzado |
| T3 | Velocidad en curva; anticipación; aceptación de brecha y tiempo de llegada; prioridades; inicio y fin de la zona de un cruce; convergencia; no conflicto entre carriles opuestos |
| T6 | Velocidad de marcha (media 1.35 ± 0.01, DE 0.20 ± 0.015, rango, N = 20 000); misma semilla → misma secuencia; inicio del cruce con semáforo (casos borde); paso cebra frente a autos; disparo del cruce fuera del paso; desplazamiento lateral de la pasada cercana |
| Fuerza social | Impulso hacia la velocidad deseada; repulsión decreciente con la distancia y en sentido contrario al otro; anticipación (uno que viene de frente empuja más que uno quieto); anisotropía; límite de velocidad; sin superposición de frente, cruzándose y frente a un peatón detenido, sin trabarse |
| T4 | Lognormal: mediana y rango intercuartil empíricos dentro de ±5 % con N = 10 000; misma semilla → misma secuencia; perfil quíntico (extremos, aceleración nula en los bordes, aceleración máxima ≤ *a*lat,max); posición lateral requerida y distancia libre; TTC con el de frente; sin desplazamiento lateral detenido y ángulo ≤ 15° en toda la maniobra (2026-10-13) |

### 17.2 Pruebas de simulación

- **Semáforos.** Cola ordenada en rojo, con el primer auto detenido a 1.8 m de la línea, y salida en orden en verde. Sin entradas con rojo encendido más de 1.5 s.
- **Derecho de paso.** Monitor continuo cada frame durante 4 minutos (31 120 frames, 30–80 autos): **0 pares de autos a menos de 2.5 m entre centros**. El monitor detectó dos casos que llevaron a las reglas de [11.6](#116-reglas-adicionales): una convergencia derecha–izquierda y un par de conectores hermanos.
- **Nivel de tráfico.** Nivel 1: 30/30 autos estable durante 4 min, con *p* = 0.30. Al subir a nivel 2 en plena sesión: *p* bajó a 0.08 y la cantidad llegó a 60 en ~90 s, volviendo después a *p* = 0.30.
- **Adelantamiento (T4).** Bici detenida o en movimiento, en la ciclovía, junto a la solera y dentro del carril, con nivel de tráfico 2 (60 autos) y el monitor continuo de pares de autos:

  - **Distancias.** En todas las pasadas con maniobra (`in_lane`, `one_way`, `opposing`) la distancia mínima real coincidió con la planificada: diferencia máxima de 0.006 m en más de 20 pasadas (p. ej., 3.00 → 3.001, 2.69 → 2.696, 1.65 → 1.648, 0.92 → 0.917). Sin maniobra, la distancia real fue la geométrica (≥ la sorteada).
  - **Corrida limpia final** (22 391 frames): 0 pares de autos a menos de 2.5 m, 0 choques con el ciclista; 1 aborto, seguro (el auto aún detrás de la bici).
  - **Problemas encontrados y corregidos en esta validación:** volver al centro antes de pasar al cruzar una intersección; pasar antes de completar el desplazamiento; abortar al lado de la bici; deslizamiento lateral con el auto detenido; roce en una convergencia (zona de conflicto). Las pasadas falsas tras un teletransporte también se corrigieron.

- **Peatones (T6).** 47 peatones: velocidad media en marcha 1.31–1.33 m/s; esperan y cruzan con la fase peatonal; los autos ceden.

  - **Monitor de seguridad** (3 min, nivel de tráfico 2, cada frame): 0 acercamientos a menos de 0.3 m entre un auto y un peatón en la calzada (26 392 frames de peatones cruzando) y 0 pares de autos a menos de 2.5 m.
  - **Pasada cercana:** planificada 0.50 → real 0.526 m; 1.00 → 1.17 m antes del ajuste de línea; 2.00 → 2.026 m.
  - **Cruce fuera del paso:** disparado con TTA = 3.01 s para *t*\* = 3.03 s. El ciclista simulado no frena y el choque activó el reinicio, como se espera.

- **Arreglos del 2026-10-13** (Play, nivel de tráfico 1, ciclista simulado pedaleando, monitores cada frame):

  - **Animación de peatones:** desplazamiento máximo del hueso raíz respecto del peatón 0.054 m (antes 1.63 m) y 0 saltos > 0.15 m en 591 706 muestras.
  - **Autos:** 0 desplazamientos laterales con el auto detenido; en 1 373 muestras durante adelantamientos, 0 con más de 3° entre la carrocería y la dirección de avance (en total 6 de 136 690, todas al reaparecer un auto del pool).
  - **Detención en la línea:** frente del auto a 0.32–0.53 m de la línea (media 0.44 m, 6 detenciones con semáforo o PARE); antes ~1.8 m.
  - **Seguridad:** 0 pares de autos a menos de 2.5 m (13 615 frames) y 0 acercamientos auto–peatón < 0.3 m en 37 610 frames de peatones en la calzada.

- **Segunda ronda del 2026-10-13** (adelantamientos y pasos):

  - **Adelantamientos:** bici dentro del carril en las rectas del anillo y en las calles de entrada, nivel 2: 4 adelantamientos iniciados y 0 autos trabados más de 5 s (antes un auto quedaba indefinidamente detenido en «Shifting»); distancia real 3.11 m para 3.00 planificada; un auto detenido detrás de la bici detenida junto a la solera la pasó a 2.50 m.
  - **Interbloqueo y altura** (3 min, nivel 2): 0 autos detenidos por un peatón más de 15 s, 0 peatones esperando a un auto más de 10 s y 0 frames con peatones esperando a un auto sobre el paso (ya no quedan autos detenidos encima); altura máxima del peatón sobre el asfalto al cruzar 0.000 m en 40 657 muestras (antes 0.225 m); 0 acercamientos auto–peatón < 0.3 m (42 399 frames) y 0 pares de autos < 2.5 m.
  - **Bici detenida en calle de doble sentido** (75 s cerca del semáforo de I_NO y 75 s a mitad de cuadra): a mitad de cuadra 5 adelantamientos de 5 sin abortos, espera máxima 17 s; cerca del semáforo 3 adelantamientos, espera máxima 5.5 s (antes los autos esperaban indefinidamente); 0 autos superpuestos.
  - **Pasos peatonales:** 0 acercamientos auto–peatón < 0.3 m en 58 574 frames de peatones en los pasos (incluido yielding_car) y 0 pares de autos a menos de 2.5 m (29 298 frames).

- **Fuerza social y cruces** (2026-10-13; 150 s, nivel de tráfico 2, 60 autos y 47 peatones, monitores cada frame):

  - **Cruces:** 42 completados. Duración mediana 9.1 s, percentil 90 11.0 s, máximo 11.7 s (antes del arreglo de la rampa: 7 cruces, máximo 74 s).
  - **Marcha:** velocidad media 1.29 m/s.
  - **Peatones:** distancia mínima entre centros 0.50 m (0 superposiciones), 0 fuera del NavMesh, 0 en la calzada sin permiso, 0 trabados.
  - **Autos:** 0 superpuestos (con sus posiciones reales, incluido el desplazamiento lateral), 0 detenidos en el carril contrario, 0 detenidos por un peatón más de 15 s.
  - **Auto–peatón:** 0 acercamientos a menos de 0.3 m.
  - **Rendimiento en el Editor:** 8.2 ms por frame, 6.1 ms del hilo principal.

- **Rendimiento (Editor).** T2: ~3.8 ms de CPU por frame. T4 con 60 autos: 7.8 ms por frame, 2.4 ms del hilo principal. T6 con 60 autos y 47 peatones: 12.1 ms por frame, 6.0 ms del hilo principal. **[PENDIENTE]** medir en el visor con el Profiler (objetivo: 11.1 ms por frame a 90 fps).

---

## 18. Decisiones del equipo

| Fecha | Tema | Decisión |
|---|---|---|
| 2026-10-02 | Control de versiones | Sin git. Respaldos de la escena y del código antes de cada fase en `C:\Lab\Simulacion\Backups\` |
| 2026-10-02 | Tests | Ensamblado `SimBici` con su ensamblado de tests |
| 2026-10-02 | Serialización de la escena | Reserializar solo `test.unity` (Unity la mantuvo en binario) |
| 2026-10-02 | Semáforos (T2) | En I_NO e I_SO; solo los accesos con llegada (N, E, O); 25/3/2 s |
| 2026-10-02 | Ruta del ciclista | Red con todos los caminos posibles, por ciclovía en su sentido o junto a la solera derecha |
| 2026-10-02 | Calles sin ciclovía | El ciclista va casi pegado a la vereda (0.75 m de la solera) |
| 2026-10-02 | Pesos de giro (T3) | 70/20/10 |
| 2026-10-02 | Prioridad sin semáforo | Prioridad del circuito; PARE en la calle x = 4. La brecha depende del conductor |
| 2026-10-02 | Señales sobrantes | Desactivarlas |
| 2026-10-02 | Demanda | Sin spawners en el anillo; entradas solo desde afuera, propensas a entrar al anillo; una calle de salida regulada; cantidad estable según el nivel |
| 2026-10-07 | Adelantamiento (T4) | El mismo modelo en todas las calles (opción «a»); corregir las curvas para que la distancia en la ciclovía sea pareja |
| 2026-10-07 | Documentación | Mantener este documento actualizado en cada paso |
| 2026-10-07 | Orden de fases | Hacer T6 antes que T5 |
| 2026-10-07 | Avatares | Descargar 16 avatares Rocketbox (~1.1 GB) |
| 2026-10-07 | Pasos peatonales | Todos los brazos de I_NO e I_SO + un paso cebra en la recta norte |
| 2026-10-13 | Paso cebra | Quitarlo (quedaba casi junto al semáforo de I_NO) |
| 2026-10-07 | Densidad peatonal | Configurable (por defecto 2 / 100 m) |
| 2026-10-13 | Arreglos tras el visor | Corregir el «lag» de los peatones, el deslizamiento y el zigzag de los autos y la ubicación de semáforos y líneas de detención. Social Force Model después |
| 2026-10-13 | Adelantamientos | Que adelanten a la bici detenida en calles de doble sentido, sin quedar en el carril contrario esperando el semáforo |
| 2026-10-13 | Peatones | Implementar el Social Force Model |

---

## 19. Inventario de archivos

| Ruta | Rol |
|---|---|
| `scripts/traffic/IntelligentDriverModel.cs` | Ecuación IDM e integración balística |
| `scripts/traffic/DriverProfile.cs` (+ `Profiles/DriverProfile.asset`) | Distribuciones por conductor |
| `scripts/traffic/TrafficLane.cs` | Carril, conexiones, movimiento, conflictos, espacio lateral |
| `scripts/traffic/TrafficVehicle.cs` | Estado del auto |
| `scripts/traffic/TrafficSpawner.cs` | Llegadas |
| `scripts/traffic/TrafficManager.cs` | Motor del tráfico |
| `scripts/traffic/TrafficRules.cs` | Reglas puras de T3 |
| `scripts/traffic/BikeConflictZone.cs` | Cruces giro–bici |
| `scripts/traffic/RingExitController.cs` | Regulación de la salida |
| `scripts/traffic/OvertakeRules.cs` | Reglas puras de T4 (lognormal, quíntico, distancias, TTC) |
| `scripts/experiment/OvertakeMonitor.cs` | Medición de cada pasada (`overtake_pass`) |
| `scripts/Editor/LaneRoomAnalyzer.cs` | Espacio lateral: solera izquierda y carril contrario |
| `scripts/pedestrian/PedestrianRules.cs` | Reglas puras de T6 |
| `scripts/pedestrian/SocialForce.cs` | Modelo de fuerza social (reglas puras) |
| `scripts/pedestrian/PedestrianManager.cs` | Peatones ambientales |
| `scripts/pedestrian/PedestrianAgent.cs` | Estado y visual del peatón |
| `scripts/pedestrian/Crosswalk.cs` | Paso peatonal (con semáforo o cebra) |
| `scripts/pedestrian/PedestrianSignalLight.cs` | Semáforo peatonal visual |
| `scripts/pedestrian/PedestrianEvent.cs` | Eventos de peatones (pasada cercana, cruce fuera del paso) |
| `scripts/Editor/PedestrianSetupEditor.cs` | `SimBici > Build Pedestrian Setup` |
| `scripts/Editor/RocketboxImportPostprocessor.cs` | Ajustes de importación de Rocketbox |
| `ThirdParty/Rocketbox/` | Avatares, animaciones, materiales y controladores |
| `AssetsLab/Pedestrians/` | Prefabs de peatones y materiales de pasos y semáforos peatonales |
| `scripts/trafficLight/SignalTiming.cs` | Tipos, ciclo semafórico y regla del amarillo |
| `scripts/trafficLight/IntersectionController.cs` | Controlador semafórico |
| `scripts/trafficLight/ResearchTrafficLight.cs` | Luz visual y halo |
| `scripts/trafficLight/StopLine.cs` | Línea de detención y eventos del ciclista |
| `scripts/trafficLight/IntersectionLaneBuilder.cs` | Datos del generador de intersecciones |
| `scripts/experiment/CyclistPath.cs`, `CyclistNetwork.cs`, `RiderRoute.cs` | Red del ciclista y seguimiento |
| `scripts/experiment/ExperimentLogger.cs`, `EventBus.cs`, `CrashResetHandler.cs` | Registro y reinicio tras choque |
| `scripts/Editor/IntersectionLaneBuilderEditor.cs` | Generación de intersecciones (conflictos, curvas) |
| `scripts/Editor/CyclistNetworkEditor.cs` | Generación de la red del ciclista |
| `scripts/Editor/LaneCurbAligner.cs` | Alineación de carriles a la solera |
| `scripts/Editor/LaneSmoother.cs` | `SimBici > Smooth Lanes`: suavizado del rumbo de carriles y caminos |
| `scripts/Editor/IntersectionCurbFitter.cs` | `SimBici > Fit Intersections To Curbs`: líneas, pasos y postes según las soleras |
| `ThirdParty/Rocketbox/Animations/InPlace/` | Clips de caminar en el lugar |
| `Tests/EditMode/*.cs` | Tests |
| `AssetsLab/CarTest.prefab`, `AssetsLab/Interseccion.prefab`, `AssetsLab/TrafficLights/*` | Prefabs y materiales del halo |

---

## 20. Historial de cambios

### 2026-10-02: base de la red y Fase 0

- Carriles conectados en las 5 intersecciones del circuito (conectores manuales, después reemplazados por el builder).
- Spawners en los carriles de entrada; multiplicador global de tráfico.
- **Fase 0:**
  - *pool* de objetos por prefab;
  - velocidad del ciclista proyectada sobre el carril;
  - largo y ancho de `CarTest` alineados con su collider;
  - ensamblados y 11 tests;
  - spawner que no aparece delante de autos que vienen llegando.

### 2026-10-02: Fase T2 (semáforos)

- Controlador semafórico determinista con todo-rojo, desfase, 3 o 4 brazos y fases peatonales.
- Líneas de detención con regla de amarillo y retardo de arranque opcional.
- Generador de intersecciones (`IntersectionLaneBuilder`).
- Halos para VR.
- Red de caminos del ciclista (`CyclistNetwork`) y `RiderRoute` reescrito para seguir al ciclista sobre la red.
- Columna `route_path` en el log; evento `cyclist_intersection_exit`.

### 2026-10-02: Fase T3 (giros y derecho de paso)

- Velocidad en curva y anticipación.
- Puntos de conflicto con prioridades; aceptación de brecha por conductor; PARE.
- No bloquear la intersección; compromiso de entrada; regla del punto de conflicto dentro de la intersección; conectores hermanos; antibloqueo.
- Cesión al ciclista en los giros.
- Las tres intersecciones sin semáforo pasan al builder. Señales PARE nuevas; señales sobrantes desactivadas.
- 7 tests nuevos (27 en total).

### 2026-10-02: demanda estable

- Eliminados los spawners del anillo.
- Cantidad objetivo según el nivel; tope en los spawners; `RingExitController`.
- Pesos por acceso; precalentamiento de 120 s con flujo ×3.

### 2026-10-07: Fase T4 (adelantamiento ambiental)

- Perfil transversal del circuito y `SimBici > Align Lanes To Curb`. La distancia manubrio–auto en la ciclovía quedó en 1.76–1.82 m.
- Camino del ciclista por el centro de la ciclovía, medido desde la solera.
- `OvertakeRules`; `SimBici > Analyze Lane Room` (solera izquierda y carril contrario).
- Maniobra lateral en `TrafficManager`: decisión, perfil quíntico con ritmo según la velocidad, aborto seguro y reacción de los autos de frente.
- Nuevos parámetros de *t*follow en `DriverProfile`.
- `OvertakeMonitor` y eventos `overtake_start`, `overtake_abort` y `overtake_pass`.
- Los conflictos de las intersecciones pasan a ser zonas (inicio y fin) en vez de puntos.
- 6 tests nuevos (33 en total).
- Este documento.

### 2026-10-07: Fase T6 (peatones)

- Descarga parcial de Rocketbox (16 avatares y 8 animaciones) e importador automático.
- Paquete `com.unity.ai.navigation`; áreas Vereda y Cruce; NavMesh en dos pasadas.
- 9 pasos peatonales con pintura y semáforos peatonales; los autos ceden en los pasos (desde el 2026-10-13, 8: se quitó el paso cebra).
- `PedestrianManager` (densidad configurable, marcha de Knoblauch et al., cruces según semáforo o cebra).
- `PedestrianEvent` (pasada cercana y cruce fuera del paso) con medición de la distancia mínima real.
- Peatones fuera de un paso vistos por los autos.
- 6 tests nuevos (39 en total).

### 2026-10-13: arreglos tras la prueba en el visor

- **Peatones «con lag»:** clips de caminar en el lugar y velocidad de reproducción por clip ([14.1](#141-avatares)). Los controladores de animación se reutilizan al regenerar (antes quedaban referencias nulas hasta reimportar).
- **Deslizamiento al adelantar:** maniobra lateral acoplada al avance, con ángulo máximo de 15°, y auto orientado según su trayectoria ([13.5](#135-maniobra-lateral)).
- **Zigzag en el carril:** suavizado del rumbo de los carriles y de la red del ciclista ([4.3](#43-perfil-transversal-del-circuito)).
- **Semáforos y líneas de detención:** ajuste a las soleras reales, líneas pintadas, postes sobre la vereda, pasos fuera de las esquinas y autos detenidos a 0.5 m de la línea ([11.8](#118-ajuste-a-las-soleras-líneas-de-detención-pasos-y-postes), [10.3](#103-conducta-de-los-autos-en-la-línea-de-detención)).
- 2 tests nuevos (41 en total).
- **Segunda ronda:**
  - **Adelantamientos:** los autos habían dejado de adelantar, trabados detrás de la bici porque no podían correrse sin avanzar. Se corrigió con la anticipación de la maniobra ([13.4](#134-cuándo-el-ciclista-es-obstáculo)).
  - **Paso cebra:** se quitó el de la recta norte.
  - **Cesión en los pasos peatonales:** corregida ([14.5](#145-reglas-de-cruce)).
- **Tercera ronda:**
  - **Interbloqueo auto–peatón:** el auto que ya está sobre el paso ahora termina de pasar.
  - **Salida:** el auto no entra a la intersección sin espacio pasado el paso de salida.
  - **Altura:** los peatones caminan a nivel del asfalto al cruzar.
  - **Bici atravesada:** se considera su largo a lo ancho del carril.
- **Cuarta ronda (bici detenida en calle de doble sentido):**
  - **TTC:** la maniobra se estima con la velocidad de salida.
  - **Decisión:** inmediata frente a una bici detenida.
  - **Espera:** el auto queda a la distancia que necesita para salir.
  - **Ángulo:** el máximo depende de la velocidad (30° detenido, 15° desde 5 m/s).
- **Quinta ronda (carril contrario y superposiciones):**
  - **Intersección:** el adelantamiento debe terminarse antes de la intersección y no se adelanta a través de un cruce.
  - **Tráfico de frente:** el TTC se calcula por el trazado del carril contrario, el que adelanta también frena ante él, y el tiempo de maniobra se estima con aceleración.
  - **Bici detenida:** el auto retrocede para salir; tras un aborto puede decidir de nuevo; y si la bici está detenida en la salida, el auto no entra al cruce.
  - **Superposiciones de autos:** se corrigieron tres causas:
    - autos que asoman desde el carril siguiente;
    - superposición de conectores hermanos calculada con la geometría;
    - el antibloqueo ya no ignora la ocupación.
  - **Pasos peatonales:** sin espacio pasado el paso, el auto no queda detenido sobre él.
- **Social Force Model** para los peatones ([14.4](#144-peatones-ambientales-pedestrianmanager)), con 8 tests nuevos (49 en total).

---

## 21. Referencias

- Beck, B., Chong, D., Olivier, J., Perkins, M., Tsay, A., Rushford, A., Li, L., Cameron, P., Fry, R., & Johnson, M. (2019). How much space do drivers provide when passing cyclists? Understanding the impact of motor vehicle and infrastructure characteristics on passing distance. *Accident Analysis & Prevention, 128*, 253–260. https://doi.org/10.1016/j.aap.2019.03.007
- Gonzalez-Franco, M., et al. (2020). The Rocketbox library and the utility of freely available rigged avatars. *Frontiers in Virtual Reality*. (Peatones, T6.)
- Helbing, D., & Molnár, P. (1995). Social force model for pedestrian dynamics. *Physical Review E, 51*(5), 4282–4286. https://doi.org/10.1103/PhysRevE.51.4282 (Peatones, fuerza social.)
- Johansson, A., Helbing, D., & Shukla, P. K. (2007). Specification of the social force pedestrian model by evolutionary adjustment to video tracking data. *Advances in Complex Systems, 10*(supp02), 271–288. https://doi.org/10.1142/S0219525907001355 (Especificación elíptica II.) **[VERIFICAR]** parámetros calibrados.
- Knoblauch, R. L., Pietrucha, M. T., & Nitzburg, M. (1996). Field studies of pedestrian walking speed and start-up time. *Transportation Research Record, 1538*. https://doi.org/10.3141/1538-04 (Peatones, T6.)
- Nazemi, M., et al. (2021). *Accident Analysis & Prevention, 151*, 105943. https://doi.org/10.1016/j.aap.2020.105943 (Citada en el plan.)
- Rasch, A., Boda, C.-N., Thalya, P., Aderum, T., Knauss, A., & Dozza, M. (2020). How do oncoming traffic and cyclist lane position influence cyclist overtaking by drivers? *Accident Analysis & Prevention, 142*, 105569. https://doi.org/10.1016/j.aap.2020.105569
- Treiber, M., Hennecke, A., & Helbing, D. (2000). Congested traffic states in empirical observations and microscopic simulations. *Physical Review E, 62*, 1805–1824.
- Treiber, M., & Kanagaraj, V. (2015). Comparing numerical integration schemes for time-continuous car-following models. *Physica A, 419*, 183–195.
- Transportation Research Board. *Highway Capacity Manual*. **[VERIFICAR]** edición y tabla de brechas críticas para intersecciones con PARE.
- Ministerio de Transportes y Telecomunicaciones (Chile). *Manual de Señalización de Tránsito*, Capítulo 4: Semáforos. **[VERIFICAR]** duración del amarillo y del despeje.

> **Nota:** los autores completos de Beck et al. (2019), Rasch et al. (2020) y Nazemi et al. (2021) deben verificarse con el DOI antes de publicar.
