🌐 [English](README.md) · **Español**

# Tests end-to-end

Cada test levanta la pila real (PostgreSQL + JoinServer + DataServer + GameServer,
más ConnectServer en `full_chain`) y la ejercita con un cliente que habla el
protocolo binario auténtico, cifrado incluido.

## Requisitos

* **PostgreSQL** instalado (cualquier versión reciente; probado con 16.14).
  En Windows: `winget install PostgreSQL.PostgreSQL.16`.
* **SDK de .NET 10**.
* El paquete original completo al lado del repo: `MuClient/Data/` (claves de
  cifrado) y `MuServer99B/Data/` (mapas, monstruos, items, eventos).

No hace falta configurar nada más. `_env.py` descubre PostgreSQL y `dotnet`
solos, resuelve las rutas del repo desde su propia ubicación, y compila los
proyectos que el test necesita antes de usarlos.

## Correrlos

```bash
python tests/gameserver_fase4_e2e_test.py
```

Salen 0 si pasan y 1 si falla alguna aserción. Los `[OK]`/`[FALLO]` del cliente
van a stdout, y al final se vuelca el log de cada servidor.

## Aislamiento

Los tests **no tocan** una instalación de PostgreSQL existente ni necesitan sus
credenciales: cada corrida hace `initdb` de un cluster desechable en el
directorio temporal, lo levanta en un puerto libre con autenticación `trust` en
loopback, y lo apaga al terminar. Los puertos de los cuatro servidores también se
reservan dinámicamente, así que dos corridas seguidas no se pisan aunque una
haya dejado un proceso colgado.

## Estado conocido

`full_chain` y `fase1` pasan enteros. El resto llega lejos (16-36 aserciones en
verde, incluida toda la parte de protocolo) y falla en **un** punto, siempre el
mismo: la secuencia de combate del `WorldTestClient`.

La causa está diagnosticada y **no es de protocolo**: el monstruo de prueba
contraataca y Hero1, con los 60 HP de un personaje recién creado, se muere en
algún punto del recorrido; a partir de ahí no puede seguir atacando y el test
espera un paquete que no va a llegar. Subirle la vida por fixture no alcanza
(`RecalcCombatStats` recalcula `MaxLife` desde `Vitality` al entrar al mundo) y
subirle `Vitality` desbalancea el resto (con 100 sube a nivel 26 de una sola
muerte y rompe otros pasos). Es balance del servidor, no del test.

Al arreglar la detección de muerte se destapó esto: antes el bucle daba por
muerto al monstruo con solo ver un `0x17`, sin mirar **qué índice** murió. Como
el servidor usa el mismo head para avisar que murió un jugador, el test cantaba
`[OK] Mató al monstruo` cuando en realidad se había muerto el personaje y el
monstruo estaba intacto. Ahora compara el índice y falla de verdad cuando el
combate no sale.

## Notas de fixture que importan

* **El monstruo de prueba tiene que ser el único.** El bloque de ataque del
  `WorldTestClient` es compartido, no está gateado por argumentos, y ataca el
  índice 0 dando por sentado que es el monstruo de prueba. El GameServer publica
  sus 29 archivos de spawn reales en su propio `Data/`, y entre ellos está
  `000 - Lorencia.txt`, que ordena antes que cualquier `000 - Test.txt`. Por eso
  `seed_test_monster()` vacía el directorio de spawns antes de escribir el suyo.
* **La salida de compilación se despliega primero y el fixture encima.** Al
  revés, el `Data/` del GameServer pisa en silencio los archivos recortados que
  el test acaba de escribir.
* **Los servidores necesitan un stdin abierto.** Los tres corren un bucle
  `Console.ReadLine()` para sus comandos de consola y terminan cuando devuelve
  null. Heredar un stdin ya cerrado los mata apenas arrancan, justo después de
  loguear que están listos: parece un problema de red y no lo es.
* **`MaxConnectionPerIP` es obligatorio en `ConnectServer.ini`.**
  `IpConnectionTracker.CheckIpAddress` rechaza la primera conexión de una IP
  cuando el límite es 0 (compatibilidad con el original), así que sin esa clave
  el ConnectServer acepta el socket y lo corta enseguida.
