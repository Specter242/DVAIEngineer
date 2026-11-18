# AI Engineer: changelog

## 0.15.3 (2026-11-18)

- Your engineer keeps the loco in front on the road. A train with its cars
  ahead of the loco used to shove them all the way to the next station. Now
  it turns on a wye or loop if one is within reach and runs loco first,
  backing up only at the end, where a track needs the cars first. Short
  shoves in a yard work as before.
- Sent to pick up cars, a loco no longer goes the long way round just to
  arrive facing away from them. If turning would add more than about 1 km,
  it couples on facing the cars.
- A loco with only its tender or a second unit now counts as running light.
- Fixed: a steam loco with a tender could wait forever at "Releasing the
  brakes".
- Fixed: with Double Track, the tablet's route map page couldn't find its
  switches, so it could miss which way they were set, your engineer's route,
  and trains out on the line.
- The Comms Tablet's Engineers app has all of these too.

## 0.15.2 (2026-11-17)

- Fixed: a loco that isn't yours (a company loco, or one you spawned), picked
  on the tablet's map with "Give orders to it", now stays picked and gets its
  plate, so its first order gives it an engineer. Before, the tablet dropped
  it within a second and the order went to one of your other engineers. The
  Comms Tablet's Engineers app has the same fix.
- The tablet's top bar always shows the plate of the loco you're giving
  orders to, even when you have more engineers than fit.
- The map has a clean, flat background, as the Comms Tablet's own map has,
  instead of the booklet's worn paper and the route map's stained grid, and
  its title is darker so it reads on it. On the Comms Tablet's Engineers app
  too.

## 0.15.1 (2026-11-17)

First public release.

- Give a locomotive an engineer: send it to a track, a cut of cars, or a
  spot on the map, and it finds the route, sets the switches, and drives
  there while you run another train. Queue more orders behind it.
- Orders: go to a track (cars first or loco first), couple to either end
  of a cut, set out cars, back or ahead by car lengths, stop here, hand
  back. Fuel and Service send it to the nearest service station.
- Hand over jobs, one or several at once. It fetches each job's cars, from
  several tracks or from behind other cuts, sets them out on their tracks,
  and plans the order with the least running. Shunting jobs are worked step
  by step, waiting at the warehouse machine for you.
- The engineer's tablet: held, or fitted to a large gadget mount. It comes
  in your starting kit once per save, is sold beside the clinometer, and F9
  puts one in front of you. Its pages are a map (the game's station
  booklet), your engineers, and your jobs. With the Comms Tablet
  installed, they are its Engineers app instead.
- The Comms Radio's AI ENGINEER mode, in VR too.
- Diesel, electric, and steam. On steam it works the regulator, cutoff, and
  cylinder cocks. With Realistic Firing (newer than 1.23.0) it puts the
  automatic fireman to work; on stock steam it fires the loco itself.
- A steam helper in your own train, worked by your whistle: two long to go,
  one short to stop, three short to back.
- With Realistic Brakes it waits for the air and sets the train brake as a
  crew would.
- It keeps to the speed limits, never throws a switch under a car, couples
  at walking pace, and eases off when its wheels slip.
- Works with Skin Manager: the tablet is kept in saves and comes from the
  shops.
