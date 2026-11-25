// bike_input.ino
// Envia datos crudos por serial. Sin transformaciones: toda la
// calibracion y el filtrado viven en C#.
//
// Formato: pulse_count,pulse_timestamp_us,now_us,pot_raw
//
// CAMBIO respecto de la version anterior: se agrega now_us. Sin ese
// campo, Unity no puede saber cuanto hace que ocurrio el ultimo pulso
// en el reloj del Arduino y tiene que estimarlo acumulando
// Time.fixedDeltaTime, lo que arrastra todo el jitter del driver USB
// (el CH340 de los Uno clonicos bufferea hasta ~16 ms en Windows).
// Con now_us el intervalo "tiempo sin pulso" queda medido enteramente
// en el reloj del Arduino, que es estable.

const byte HALL_PIN = 2;  // INT0
const byte POT_PIN = A0;

const unsigned long BAUD_RATE = 115200;
const unsigned long SEND_INTERVAL_US = 11111;  // ~90 Hz

// Lecturas del potenciometro que se promedian en cada envio. Cada
// analogRead tarda ~0.11 ms, asi que 8 son ~0.9 ms de los 11 ms del
// ciclo. Promediar baja el temblor de +-1 cuenta del ADC con el
// manubrio quieto. El valor enviado sigue en la escala 0-1023, asi que
// la calibracion de Unity (potRawMin/Center/Max) no cambia.
const byte POT_OVERSAMPLE = 8;

// Intervalo minimo aceptable entre pulsos Hall. Con 6 imanes y ~2.1 m
// de circunferencia, cada pulso son ~0.35 m: 8 ms implicaria ~44 m/s
// (158 km/h). Cualquier cosa mas rapida es ruido electrico en el cable,
// no un iman. Filtro barato contra falsos flancos que inflarian la
// velocidad estimada. OJO: con un sensor en el rodillo (muchos mas
// pulsos por metro) hay que bajar este valor.
const unsigned long MIN_PULSE_INTERVAL_US = 8000;

volatile unsigned long pulseCount = 0;
volatile unsigned long lastPulseTimestampUs = 0;

unsigned long nextSendTimeUs = 0;

void setup() {
  Serial.begin(BAUD_RATE);

  // INPUT (no INPUT_PULLUP): el sensor Hall bipolar latching pierde
  // flancos con el pull-up interno activado -- ya validado con sketch
  // de diagnostico aislado.
  pinMode(HALL_PIN, INPUT);

  // CHANGE: el sensor bipolar late (alterna high/low) con cada iman
  // que pasa (polaridad N-S-N-S alternada) -> un flanco por iman.
  attachInterrupt(digitalPinToInterrupt(HALL_PIN), onHallPulse, CHANGE);

  nextSendTimeUs = micros();
}

void loop() {
  unsigned long nowUs = micros();

  // Se acumula el intervalo en vez de reasignar nowUs: reasignar hace
  // que el periodo real sea SEND_INTERVAL_US + overhead del loop, lo
  // que deriva lentamente por debajo de 90 Hz. Acumular mantiene la
  // cadencia media exacta.
  if ((long)(nowUs - nextSendTimeUs) >= 0) {
    nextSendTimeUs += SEND_INTERVAL_US;

    // Si por algun motivo se perdio mas de un slot (bloqueo largo),
    // se resincroniza en vez de disparar una rafaga de envios.
    if ((long)(nowUs - nextSendTimeUs) >= (long)SEND_INTERVAL_US) {
      nextSendTimeUs = nowUs + SEND_INTERVAL_US;
    }

    sendData(nowUs);
  }
}

void sendData(unsigned long nowUs) {
  // Copia atomica del estado del ISR: unsigned long en AVR de 8 bits
  // no se lee en una sola instruccion.
  unsigned long pulseCountSafe;
  unsigned long lastPulseTimestampUsSafe;

  noInterrupts();
  pulseCountSafe = pulseCount;
  lastPulseTimestampUsSafe = lastPulseTimestampUs;
  interrupts();

  // Promedio redondeado de POT_OVERSAMPLE lecturas, 0-1023.
  // Las interrupciones del Hall siguen activas mientras se lee.
  unsigned int potSum = 0;
  for (byte i = 0; i < POT_OVERSAMPLE; i++) potSum += analogRead(POT_PIN);
  int potRaw = (potSum + POT_OVERSAMPLE / 2) / POT_OVERSAMPLE;

  Serial.print(pulseCountSafe);
  Serial.print(',');
  Serial.print(lastPulseTimestampUsSafe);
  Serial.print(',');
  Serial.print(nowUs);
  Serial.print(',');
  Serial.println(potRaw);
}

void onHallPulse() {
  unsigned long nowUs = micros();

  // Resta en aritmetica unsigned: maneja el overflow de micros()
  // (~71 min) sin logica extra.
  if (nowUs - lastPulseTimestampUs < MIN_PULSE_INTERVAL_US) return;

  pulseCount++;
  lastPulseTimestampUs = nowUs;
}
