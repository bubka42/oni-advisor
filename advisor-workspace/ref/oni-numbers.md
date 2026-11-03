# ONI numbers (base game): use these, don't guess

If a number you need isn't here and you aren't sure, say it's approximate. A cycle is 600 s.

Duplicants
- Breathe 100 g/s oxygen each and exhale 2 g/s CO2. Eat 1000 kcal per cycle each.

Oxygen
- Electrolyzer: 1 kg/s water → 888 g/s oxygen + 112 g/s hydrogen, 120 W. Outputs gas at ≥70 °C. Stops at "Max Gas
  Pressure" when surrounding gas reaches 1.8 kg/tile.
- Oxygen Diffuser: 550 g/s algae → 500 g/s oxygen, 120 W.
- Algae Terrarium: 30 g/s algae + 300 g/s water → 40 g/s oxygen, outputs polluted water; no power. A stopgap.
- Carbon Skimmer: 1 kg/s water absorbs 300 g/s CO2 → 1 kg/s polluted water, 120 W.

Power
- Manual Generator 400 W (a dupe runs it). Coal Generator 600 W, burns 1 kg/s coal, 20 g/s CO2.
- Hydrogen Generator 800 W, burns 100 g/s hydrogen: one electrolyzer (112 g/s) slightly out-produces one.
- Battery 10 kJ, Smart Battery 20 kJ, Jumbo Battery 40 kJ; batteries lose charge slowly when idle.
- Wire 1 kW, Conductive Wire 2 kW, Heavi-Watt Wire 20 kW. Overload = what the running machines draw exceeds the
  weakest wire; generators and charging batteries don't count. A Power Transformer passes at most 1 kW to its branch.
- Draws: Electrolyzer, Gas Filter, Water Sieve, Refrigerator 120 W; Gas Pump, Liquid Pump, Microbe Musher 240 W;
  Mini Gas Pump 60 W; Research Station 60 W; Super Computer 120 W; Electric Grill 60 W; Ceiling Light 10 W.

Gas and liquid
- Gas Pump up to 500 g/s; Mini Gas Pump 50 g/s; a gas pipe packet holds at most 1 kg.
- Liquid Pump 10 kg/s; a liquid pipe packet holds 10 kg. Pitcher Pump is manual.
- Filters pass the chosen element out of the main output, the rest out of the filtered port; both must lead
  somewhere or it blocks. A full output pipe stops a pump ("Pipe Blocked"). Gas Reservoir 150 kg; Liquid 5000 kg.
- Water Sieve: 5 kg/s polluted water + 1 kg/s sand → 5 kg/s water, 120 W (germs pass through).
- Gases layer by density: hydrogen at the top, CO2 at the bottom, oxygen and polluted oxygen between.

Food
- Mealwood: 600 kcal per 3 cycles tended, 10 kg dirt per cycle. Bristle Blossom: 1600 kcal per 6 cycles, 20 kg
  water per cycle. Microbe Musher: 75 kg dirt + 75 kg water → Mush Bar (800 kcal, low quality).
- Ration Boxes don't refrigerate; a powered Refrigerator stops spoiling.

Hygiene
- Lavatory: 5 kg water per use → ~11.7 kg polluted water. Outhouse and Wash Basin need manual tending. Polluted
  water and polluted dirt carry food-poisoning germs.
