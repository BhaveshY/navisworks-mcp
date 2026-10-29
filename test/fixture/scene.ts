export type Box = {
	name: string;
	ifcClass: 'IfcWall' | 'IfcSlab' | 'IfcColumn' | 'IfcDuctSegment';
	storey: string;
	min: [number, number, number];
	max: [number, number, number];
	props: Record<string, string | number | boolean>;
};

export const storeys = [
	{ name: 'Level 1', elevation: 0 },
	{ name: 'Level 2', elevation: 3000 },
];

const wall = (name: string, min: Box['min'], max: Box['max'], fireRating: string, isExternal: boolean): Box => ({
	name, ifcClass: 'IfcWall', storey: 'Level 1', min, max, props: { FireRating: fireRating, IsExternal: isExternal },
});

const duct = (name: string, y: number, x0: number, x1: number, system: string): Box => ({
	name, ifcClass: 'IfcDuctSegment', storey: 'Level 1',
	min: [x0, y - 150, 2350], max: [x1, y + 150, 2650], props: { System: system },
});

export const boxes: Box[] = [
	wall('W-South', [0, 0, 0], [10000, 200, 3000], 'F90', true),
	wall('W-North', [0, 5800, 0], [10000, 6000, 3000], 'F90', true),
	wall('W-West', [0, 210, 0], [200, 5790, 3000], 'F30', false),
	wall('W-East', [9800, 210, 0], [10000, 5790, 3000], 'F30', false),
	{
		name: 'S-Level2', ifcClass: 'IfcSlab', storey: 'Level 2',
		min: [0, 0, 3000], max: [10000, 6000, 3200], props: { LoadBearing: true },
	},
	{
		name: 'C-1', ifcClass: 'IfcColumn', storey: 'Level 1',
		min: [3000, 2800, 0], max: [3400, 3200, 2990], props: { LoadBearing: true },
	},
	{
		name: 'C-2', ifcClass: 'IfcColumn', storey: 'Level 1',
		min: [6600, 2800, 0], max: [7000, 3200, 2990], props: { LoadBearing: true },
	},
	duct('D-Supply-1', 1500, -500, 10500, 'Supply'),
	duct('D-Supply-2', 4500, -500, 10500, 'Supply'),
	duct('D-Return-1', 2000, -500, 10500, 'Return'),
	duct('D-Return-2', 4000, 1000, 2500, 'Return'),
];

export const overlaps = (a: Box, b: Box) =>
	a.min.every((v, i) => v < b.max[i]! && b.min[i]! < a.max[i]!);
