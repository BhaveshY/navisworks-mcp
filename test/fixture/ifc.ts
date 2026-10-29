import { writeFileSync } from 'node:fs';
import { boxes, storeys, type Box } from './scene.ts';

const GUID_CHARS = '0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz_$';

export function writeIfc(path: string) {
	const lines: string[] = [];
	let next = 1;
	const add = (entity: string) => {
		lines.push(`#${next}=${entity};`);
		return `#${next++}`;
	};
	let guidSeq = 0;
	const guid = () => {
		let n = ++guidSeq;
		let s = '';
		for (let i = 0; i < 22; i++) {
			s = GUID_CHARS[n % 64] + s;
			n = Math.floor(n / 64);
		}
		return `'${s}'`;
	};
	const real = (v: number) => (Number.isInteger(v) ? `${v}.` : `${v}`);
	const point3 = (p: number[]) => add(`IFCCARTESIANPOINT((${p.map(real).join(',')}))`);
	const str = (s: string) => `'${s.replace(/'/g, "''")}'`;

	const origin = point3([0, 0, 0]);
	const zDir = add('IFCDIRECTION((0.,0.,1.))');
	const worldAxis = add(`IFCAXIS2PLACEMENT3D(${origin},$,$)`);
	const ctx = add(`IFCGEOMETRICREPRESENTATIONCONTEXT($,'Model',3,1.E-05,${worldAxis},$)`);
	const mm = add('IFCSIUNIT(*,.LENGTHUNIT.,.MILLI.,.METRE.)');
	const units = add(`IFCUNITASSIGNMENT((${mm}))`);
	const project = add(`IFCPROJECT(${guid()},$,'Navisworks MCP fixture',$,$,$,$,(${ctx}),${units})`);
	const rootPlacement = add(`IFCLOCALPLACEMENT($,${worldAxis})`);
	const site = add(`IFCSITE(${guid()},$,'Site',$,$,${rootPlacement},$,$,.ELEMENT.,$,$,$,$,$)`);
	const building = add(`IFCBUILDING(${guid()},$,'Building',$,$,${rootPlacement},$,$,.ELEMENT.,$,$,$)`);
	add(`IFCRELAGGREGATES(${guid()},$,$,$,${project},(${site}))`);
	add(`IFCRELAGGREGATES(${guid()},$,$,$,${site},(${building}))`);

	const storeyIds = new Map<string, string>();
	for (const s of storeys) {
		const placement = add(`IFCLOCALPLACEMENT($,${add(`IFCAXIS2PLACEMENT3D(${point3([0, 0, s.elevation])},$,$)`)})`);
		storeyIds.set(s.name, add(`IFCBUILDINGSTOREY(${guid()},$,${str(s.name)},$,$,${placement},$,$,.ELEMENT.,${real(s.elevation)})`));
	}
	add(`IFCRELAGGREGATES(${guid()},$,$,$,${building},(${[...storeyIds.values()].join(',')}))`);

	const byStorey = new Map<string, string[]>();
	for (const box of boxes) {
		const id = writeBox(box);
		byStorey.set(box.storey, [...(byStorey.get(box.storey) ?? []), id]);
	}
	for (const [storey, ids] of byStorey) {
		add(`IFCRELCONTAINEDINSPATIALSTRUCTURE(${guid()},$,$,$,(${ids.join(',')}),${storeyIds.get(storey)})`);
	}

	function writeBox(box: Box) {
		const size = box.max.map((v, i) => v - box.min[i]!);
		const placement = add(`IFCLOCALPLACEMENT($,${add(`IFCAXIS2PLACEMENT3D(${point3(box.min)},$,$)`)})`);
		const profileOrigin = add(`IFCAXIS2PLACEMENT2D(${add(`IFCCARTESIANPOINT((${real(size[0]! / 2)},${real(size[1]! / 2)}))`)},$)`);
		const profile = add(`IFCRECTANGLEPROFILEDEF(.AREA.,$,${profileOrigin},${real(size[0]!)},${real(size[1]!)})`);
		const solid = add(`IFCEXTRUDEDAREASOLID(${profile},${worldAxis},${zDir},${real(size[2]!)})`);
		const shape = add(`IFCSHAPEREPRESENTATION(${ctx},'Body','SweptSolid',(${solid}))`);
		const pds = add(`IFCPRODUCTDEFINITIONSHAPE($,$,(${shape}))`);
		const element = add(`${box.ifcClass.toUpperCase()}(${guid()},$,${str(box.name)},$,$,${placement},${pds},$,$)`);
		const props = Object.entries(box.props).map(([k, v]) => {
			const value = typeof v === 'boolean' ? `IFCBOOLEAN(${v ? '.T.' : '.F.'})` : typeof v === 'number' ? `IFCREAL(${real(v)})` : `IFCLABEL(${str(v)})`;
			return add(`IFCPROPERTYSINGLEVALUE(${str(k)},$,${value},$)`);
		});
		const pset = add(`IFCPROPERTYSET(${guid()},$,'Pset_Fixture',$,(${props.join(',')}))`);
		add(`IFCRELDEFINESBYPROPERTIES(${guid()},$,$,$,(${element}),${pset})`);
		return element;
	}

	const header = [
		'ISO-10303-21;',
		'HEADER;',
		"FILE_DESCRIPTION(('ViewDefinition [ReferenceView]'),'2;1');",
		"FILE_NAME('fixture.ifc','2026-01-01T00:00:00',(''),(''),'navisworks-mcp','navisworks-mcp','');",
		"FILE_SCHEMA(('IFC4'));",
		'ENDSEC;',
		'DATA;',
	];
	writeFileSync(path, [...header, ...lines, 'ENDSEC;', 'END-ISO-10303-21;', ''].join('\n'));
}
