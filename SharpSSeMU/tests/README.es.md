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

Pasan los diez tests (`full_chain`, `fase1`-`fase6`, `grounditem`, `shop`, `skills`).

El "único fallo en la secuencia de combate" que arrastrábamos no era balance del servidor: el harness
arrancaba cada servidor con la consola conectada por pipe al test y nunca la leía mientras el test
corría. Cuando el servidor había escrito lo suficiente, el pipe se llenaba y el servidor quedaba
bloqueado dentro de `Console.WriteLine`, con el lock del log tomado, así que el test esperaba un paquete
que no se podía enviar. Ahora la consola de cada servidor va a `console.log` en su carpeta de runtime
(`%TEMP%/muservercs-e2e/rt-<tag>/<servidor>/`) y el final se imprime igual que antes.

Dos reglas en las que se apoyan los pasos de combate:

* **El golpe que mata no manda `0xD9`**, como en el `CharacterLifeCheck` original: termina en el `0x17`
  del monstruo, y su daño viaja en el paquete de recompensa `0x9C`.
  `FakeMuClient.WaitForAttackOutcomeAsync` espera cualquiera de los dos resultados.
* **`0x17` también avisa la muerte de un jugador**, así que la comprobación compara el índice que murió
  con el del monstruo. Antes, que muriera el personaje parecía "mató al monstruo".

Los fixtures siembran lo que cada escenario necesita: Hero3 (Devil Square) recibe Vitality alta **y**
vida actual, porque `Life` se lee de la fila del personaje y Vitality solo sube `MaxLife`; el test de
items de piso agrega dos monstruos deterministas que dropean (`Deployment.seed_drop_monsters`).

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
