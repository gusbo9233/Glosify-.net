// Attribute-mode animation needs both an eight-target cap and room for each
// target's vertex attributes. Prioritize speech/blinks and keep smiles symmetric.
const fallbackGroups = [['jawOpen'], ['blink'], ['smileLeft', 'smileRight'],
    ['nod'], ['mouthWide'], ['mouthRound'], ['lipsPart']];

export function bindMorphTargets(meshes) {
    const targets = new Map();
    for (const manager of new Set(meshes.map(mesh => mesh.morphTargetManager).filter(Boolean))) {
        const textureMode = manager.isUsingTextureForTargets;
        let allowed;
        if (textureMode) {
            // Keep the shader capacity stable, but skip zero-weight texture targets.
            manager.optimizeInfluencers = true;
            manager.numMaxInfluencers = manager.numTargets;
        }
        else {
            // The glTF loader reserves every slot for small managers, even at zero
            // weight. Release that reservation before enforcing the fallback budget.
            manager.optimizeInfluencers = true; manager.numMaxInfluencers = 0;
            const attributesPerTarget = ['Positions', 'Normals', 'Tangents', 'UVs', 'UV2s', 'Colors']
                .filter(kind => Array.from({ length: manager.numTargets }, (_, i) => manager.getTarget(i))
                    .some(target => target['has' + kind])).length || 1;
            let slots = Math.max(0, Math.min(8, ...meshes.filter(mesh => mesh.morphTargetManager === manager).map(mesh =>
                Math.floor((mesh.getScene().getEngine().getCaps().maxVertexAttribs - mesh.getVerticesDataKinds().filter(kind => !/^(position|normal|tangent|uv_|uv2_|color)\d+$/.test(kind)).length) / attributesPerTarget))));
            allowed = new Set();
            for (const group of fallbackGroups) if (group.length <= slots) {
                for (const name of group) allowed.add(name);
                slots -= group.length;
            }
        }
        for (let i = 0; i < manager.numTargets; i++) {
            const target = manager.getTarget(i);
            if (allowed && !allowed.has(target.name)) {
                target.influence = 0;
                continue;
            }
            const group = targets.get(target.name) || [];
            group.push(target); targets.set(target.name, group);
        }
    }
    return weights => {
        for (const [name, group] of targets) for (const target of group) target.influence = weights[name] || 0;
    };
}
