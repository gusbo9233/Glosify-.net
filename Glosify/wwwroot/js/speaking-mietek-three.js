import * as THREE from "https://cdn.jsdelivr.net/npm/three@0.180.0/build/three.module.js";

const reducedMotion = window.matchMedia("(prefers-reduced-motion: reduce)").matches;
const controllers = new WeakMap();

class MietekThreeController {
    constructor(sceneElement, host) {
        this.sceneElement = sceneElement;
        this.host = host;
        this.active = !sceneElement.hidden;
        this.talking = false;
        this.pointerLook = new THREE.Vector2();
        this.smoothLook = new THREE.Vector2();
        this.clock = new THREE.Clock();

        this.initializeRenderer();
        this.initializeScene();
        this.bindEvents();
        this.animate();

        requestAnimationFrame(() => {
            host.querySelector("[data-mietek-three-loading]")?.setAttribute("hidden", "");
            host.classList.add("is-ready");
        });
    }

    initializeRenderer() {
        this.renderer = new THREE.WebGLRenderer({
            antialias: true,
            alpha: false,
            powerPreference: "high-performance"
        });
        this.renderer.setPixelRatio(Math.min(window.devicePixelRatio, 1.8));
        this.renderer.shadowMap.enabled = true;
        this.renderer.shadowMap.type = THREE.PCFSoftShadowMap;
        this.renderer.outputColorSpace = THREE.SRGBColorSpace;
        this.renderer.toneMapping = THREE.ACESFilmicToneMapping;
        this.renderer.toneMappingExposure = 1.02;
        this.renderer.domElement.setAttribute("aria-hidden", "true");
        this.renderer.domElement.tabIndex = -1;
        this.host.prepend(this.renderer.domElement);
    }

    initializeScene() {
        this.scene = new THREE.Scene();
        this.scene.background = new THREE.Color(0x8c9499);
        this.scene.fog = new THREE.FogExp2(0x8f979c, 0.035);

        this.camera = new THREE.PerspectiveCamera(43, 1, 0.08, 45);
        this.camera.position.set(0, 2.65, 7.35);
        this.cameraTarget = new THREE.Vector3(0, 1.95, -0.35);

        this.materials = this.buildMaterials();
        this.buildEstate();
        this.bench = this.buildBench();
        this.mietek = this.buildMietek();
        this.bag = this.buildBag();
        this.buildPigeons();
        this.buildDrizzle();
        this.buildLighting();
        this.resize();
    }

    buildMaterials() {
        return {
            concrete: new THREE.MeshStandardMaterial({ color: 0x747a7e, roughness: 0.96 }),
            concreteLight: new THREE.MeshStandardMaterial({ color: 0x9ba0a1, roughness: 0.94 }),
            pavement: new THREE.MeshStandardMaterial({ color: 0x555b5e, roughness: 0.98 }),
            pavementLight: new THREE.MeshStandardMaterial({ color: 0x72787a, roughness: 0.96 }),
            grass: new THREE.MeshStandardMaterial({ color: 0x4e584a, roughness: 1 }),
            window: new THREE.MeshStandardMaterial({
                color: 0x30383e,
                metalness: 0.08,
                roughness: 0.42
            }),
            windowWarm: new THREE.MeshStandardMaterial({
                color: 0xc69f55,
                emissive: 0x6f4c1d,
                emissiveIntensity: 0.55,
                roughness: 0.46
            }),
            wood: new THREE.MeshStandardMaterial({ color: 0x60442e, roughness: 0.85 }),
            woodWorn: new THREE.MeshStandardMaterial({ color: 0x856040, roughness: 0.9 }),
            metal: new THREE.MeshStandardMaterial({ color: 0x272d30, metalness: 0.55, roughness: 0.6 }),
            skin: new THREE.MeshStandardMaterial({ color: 0xc98f61, roughness: 0.78 }),
            skinLight: new THREE.MeshStandardMaterial({ color: 0xd9a274, roughness: 0.76 }),
            cheek: new THREE.MeshStandardMaterial({
                color: 0xb9614e,
                transparent: true,
                opacity: 0.48,
                roughness: 0.86
            }),
            stubble: new THREE.MeshStandardMaterial({ color: 0x77756e, roughness: 0.94 }),
            cap: new THREE.MeshStandardMaterial({ color: 0x62594d, roughness: 0.98 }),
            capDark: new THREE.MeshStandardMaterial({ color: 0x443d34, roughness: 0.96 }),
            tracksuit: new THREE.MeshStandardMaterial({ color: 0x263a55, roughness: 0.84 }),
            tracksuitLight: new THREE.MeshStandardMaterial({ color: 0xd5d9db, roughness: 0.86 }),
            trousers: new THREE.MeshStandardMaterial({ color: 0x41474d, roughness: 0.9 }),
            shirt: new THREE.MeshStandardMaterial({ color: 0xa74635, roughness: 0.88 }),
            shoe: new THREE.MeshStandardMaterial({ color: 0x858178, roughness: 0.92 }),
            eyeWhite: new THREE.MeshStandardMaterial({ color: 0xe8e3d8, roughness: 0.7 }),
            iris: new THREE.MeshStandardMaterial({ color: 0x49382a, roughness: 0.72 }),
            mouth: new THREE.MeshStandardMaterial({ color: 0x4b211b, roughness: 0.72 }),
            tooth: new THREE.MeshStandardMaterial({ color: 0xdfd7c5, roughness: 0.75 }),
            can: new THREE.MeshStandardMaterial({
                color: 0xb39237,
                metalness: 0.62,
                roughness: 0.34
            }),
            canSilver: new THREE.MeshStandardMaterial({
                color: 0xb9bec0,
                metalness: 0.82,
                roughness: 0.28
            })
        };
    }

    mesh(geometry, material, position = [0, 0, 0], castShadow = true, receiveShadow = true) {
        const object = new THREE.Mesh(geometry, material);
        object.position.set(...position);
        object.castShadow = castShadow;
        object.receiveShadow = receiveShadow;
        return object;
    }

    addBox(parent, size, position, material, castShadow = true) {
        const object = this.mesh(
            new THREE.BoxGeometry(...size),
            material,
            position,
            castShadow,
            true);
        parent.add(object);
        return object;
    }

    addCylinder(parent, radiusTop, radiusBottom, height, position, material, segments = 24) {
        const object = this.mesh(
            new THREE.CylinderGeometry(radiusTop, radiusBottom, height, segments),
            material,
            position);
        parent.add(object);
        return object;
    }

    addBone(parent, start, end, radius, material) {
        const from = new THREE.Vector3(...start);
        const to = new THREE.Vector3(...end);
        const direction = to.clone().sub(from);
        const bone = this.mesh(
            new THREE.CylinderGeometry(radius, radius * 0.92, direction.length(), 18),
            material,
            [0, 0, 0]);
        bone.position.copy(from).add(to).multiplyScalar(0.5);
        bone.quaternion.setFromUnitVectors(
            new THREE.Vector3(0, 1, 0),
            direction.clone().normalize());
        parent.add(bone);
        return bone;
    }

    buildEstate() {
        const ground = this.mesh(
            new THREE.PlaneGeometry(24, 16),
            this.materials.pavement,
            [0, 0, -1],
            false,
            true);
        ground.rotation.x = -Math.PI / 2;
        this.scene.add(ground);

        const grassStrip = this.mesh(
            new THREE.PlaneGeometry(24, 3.4),
            this.materials.grass,
            [0, 0.012, -4.1],
            false,
            true);
        grassStrip.rotation.x = -Math.PI / 2;
        this.scene.add(grassStrip);

        const path = this.mesh(
            new THREE.PlaneGeometry(4.2, 12),
            this.materials.pavementLight,
            [-2.8, 0.024, -1.2],
            false,
            true);
        path.rotation.x = -Math.PI / 2;
        path.rotation.z = -0.13;
        this.scene.add(path);

        this.buildApartmentBlock(-5.1, 2.8, -7.2, 6.1, 5.6, 1.15, 0x777d80, 6, 7);
        this.buildApartmentBlock(2.8, 3.25, -8.3, 7.8, 6.5, 1.2, 0x858988, 8, 8);
        this.buildApartmentBlock(7.5, 2.45, -5.8, 3.6, 4.9, 1.05, 0x6f767a, 4, 6);

        const curb = this.addBox(
            this.scene,
            [13, 0.13, 0.18],
            [0, 0.065, -2.92],
            this.materials.concreteLight,
            false);
        curb.rotation.y = 0.02;

        this.buildLamp(-3.8, -2.35);
        this.buildBareTree(4.4, -2.7, 1.05);
        this.buildBareTree(-6.1, -3.1, 0.85);

        const puddleMaterial = new THREE.MeshPhysicalMaterial({
            color: 0x77858d,
            transparent: true,
            opacity: 0.62,
            roughness: 0.15,
            metalness: 0.05
        });
        const puddle = this.mesh(
            new THREE.CircleGeometry(0.85, 48),
            puddleMaterial,
            [-2.35, 0.032, 2.15],
            false,
            false);
        puddle.rotation.x = -Math.PI / 2;
        puddle.scale.set(1.8, 0.72, 1);
        this.scene.add(puddle);
    }

    buildApartmentBlock(x, y, z, width, height, depth, color, columns, rows) {
        const group = new THREE.Group();
        const facade = new THREE.MeshStandardMaterial({ color, roughness: 0.95 });
        this.addBox(group, [width, height, depth], [0, 0, 0], facade, false);
        this.addBox(
            group,
            [width + 0.08, 0.12, depth + 0.08],
            [0, height / 2 + 0.04, 0],
            this.materials.concrete,
            false);

        const windowWidth = width / (columns * 2.1);
        const windowHeight = height / (rows * 2.3);
        for (let row = 0; row < rows; row++) {
            for (let column = 0; column < columns; column++) {
                const lit = (row * 7 + column * 3 + columns) % 11 === 0;
                const window = this.addBox(
                    group,
                    [windowWidth, windowHeight, 0.035],
                    [
                        -width / 2 + (column + 0.5) * (width / columns),
                        -height / 2 + (row + 0.55) * (height / rows),
                        depth / 2 + 0.025
                    ],
                    lit ? this.materials.windowWarm : this.materials.window,
                    false);
                window.receiveShadow = false;
            }
        }
        group.position.set(x, y, z);
        this.scene.add(group);
    }

    buildLamp(x, z) {
        const lamp = new THREE.Group();
        this.addCylinder(lamp, 0.055, 0.075, 3.1, [0, 1.55, 0], this.materials.metal, 18);
        const arm = this.addBox(lamp, [0.55, 0.055, 0.055], [0.22, 3.02, 0], this.materials.metal);
        arm.rotation.z = -0.12;
        const shade = this.addBox(lamp, [0.34, 0.1, 0.22], [0.5, 2.94, 0], this.materials.metal);
        shade.rotation.z = -0.12;
        lamp.position.set(x, 0, z);
        this.scene.add(lamp);
    }

    buildBareTree(x, z, scale) {
        const tree = new THREE.Group();
        const bark = new THREE.MeshStandardMaterial({ color: 0x55483d, roughness: 1 });
        this.addCylinder(tree, 0.12, 0.2, 2.8, [0, 1.4, 0], bark, 14);
        const branches = [
            [[0, 2.15, 0], [-0.72, 3.15, 0.06]],
            [[0, 2.25, 0], [0.75, 3.3, -0.08]],
            [[-0.4, 2.72, 0.03], [-1.0, 3.28, 0.08]],
            [[0.42, 2.78, -0.03], [1.08, 3.18, -0.1]],
            [[0.02, 2.65, 0], [0.05, 3.65, 0.02]]
        ];
        branches.forEach(([start, end]) => this.addBone(tree, start, end, 0.055, bark));
        tree.position.set(x, 0, z);
        tree.scale.setScalar(scale);
        this.scene.add(tree);
    }

    buildBench() {
        const bench = new THREE.Group();
        for (let index = 0; index < 4; index++) {
            this.addBox(
                bench,
                [4.2, 0.12, 0.18],
                [0, 0.98, -0.23 + index * 0.18],
                index % 2 ? this.materials.wood : this.materials.woodWorn);
        }
        for (let index = 0; index < 3; index++) {
            const slat = this.addBox(
                bench,
                [4.2, 0.15, 0.14],
                [0, 1.42 + index * 0.23, -0.38],
                index === 1 ? this.materials.woodWorn : this.materials.wood);
            slat.rotation.x = -0.04;
        }
        [-1.65, 1.65].forEach(x => {
            this.addBox(bench, [0.12, 1.05, 0.12], [x, 0.52, -0.2], this.materials.metal);
            const foot = this.addBox(bench, [0.62, 0.1, 0.12], [x, 0.08, -0.04], this.materials.metal);
            foot.rotation.y = 0.08;
        });
        bench.position.set(0, 0, -0.15);
        this.scene.add(bench);
        return bench;
    }

    buildMietek() {
        const man = new THREE.Group();
        man.position.set(0, 0, 0.12);

        const torso = this.mesh(
            new THREE.SphereGeometry(0.72, 32, 20),
            this.materials.tracksuit,
            [0, 1.72, 0]);
        torso.scale.set(1.08, 1.05, 0.72);
        man.add(torso);
        const belly = this.mesh(
            new THREE.SphereGeometry(0.55, 28, 18),
            this.materials.tracksuit,
            [0, 1.45, 0.34]);
        belly.scale.set(1.2, 0.78, 0.72);
        man.add(belly);

        this.addBox(man, [0.075, 1.15, 0.035], [-0.63, 1.72, 0.39], this.materials.tracksuitLight);
        this.addBox(man, [0.075, 1.15, 0.035], [0.63, 1.72, 0.39], this.materials.tracksuitLight);
        this.addBox(man, [0.03, 0.78, 0.035], [0, 1.94, 0.54], this.materials.concreteLight, false);
        const shirt = this.mesh(
            new THREE.CylinderGeometry(0.2, 0.28, 0.42, 24),
            this.materials.shirt,
            [0, 2.08, 0.34]);
        shirt.rotation.x = -0.12;
        man.add(shirt);

        this.buildLeftArm(man);
        const rightArm = this.buildRightArm(man);
        this.buildLegs(man);
        const head = this.buildHead(man);

        man.userData = {
            head,
            eyes: head.userData.eyes,
            mouth: head.userData.mouth,
            tooth: head.userData.tooth,
            rightArm,
            rightArmRest: rightArm.rotation.clone()
        };
        this.scene.add(man);
        return man;
    }

    buildLeftArm(man) {
        const arm = new THREE.Group();
        this.addBone(arm, [-0.5, 2.1, 0], [-1.08, 1.87, -0.18], 0.16, this.materials.tracksuit);
        this.addBone(arm, [-1.08, 1.87, -0.18], [-1.62, 1.72, -0.28], 0.135, this.materials.tracksuit);
        const hand = this.mesh(
            new THREE.SphereGeometry(0.17, 20, 14),
            this.materials.skin,
            [-1.68, 1.7, -0.26]);
        hand.scale.set(1.2, 0.58, 0.75);
        arm.add(hand);
        man.add(arm);
    }

    buildRightArm(man) {
        const arm = new THREE.Group();
        arm.position.set(0.48, 2.08, 0.02);
        this.addBone(arm, [0, 0, 0], [0.25, -0.48, 0.18], 0.16, this.materials.tracksuit);
        this.addBone(arm, [0.25, -0.48, 0.18], [0.34, -0.83, 0.52], 0.135, this.materials.tracksuit);
        const hand = this.mesh(
            new THREE.SphereGeometry(0.17, 20, 14),
            this.materials.skin,
            [0.34, -0.88, 0.55]);
        hand.scale.set(0.82, 1.05, 0.72);
        arm.add(hand);

        const can = new THREE.Group();
        this.addCylinder(can, 0.105, 0.105, 0.36, [0, 0, 0], this.materials.can, 28);
        this.addCylinder(can, 0.103, 0.103, 0.018, [0, 0.185, 0], this.materials.canSilver, 28);
        this.addCylinder(can, 0.103, 0.103, 0.018, [0, -0.185, 0], this.materials.canSilver, 28);
        const label = this.addBox(can, [0.15, 0.13, 0.012], [0, 0.01, 0.103], this.materials.shirt, false);
        label.rotation.x = 0.02;
        can.position.set(0.34, -0.72, 0.64);
        can.rotation.x = -0.08;
        arm.add(can);
        man.add(arm);
        return arm;
    }

    buildLegs(man) {
        const legs = new THREE.Group();
        [-1, 1].forEach(side => {
            const hip = [side * 0.31, 1.34, 0.12];
            const knee = [side * 0.37, 0.92, 0.65];
            const ankle = [side * 0.39, 0.24, 0.62];
            this.addBone(legs, hip, knee, 0.21, this.materials.trousers);
            this.addBone(legs, knee, ankle, 0.185, this.materials.trousers);
            const shoe = this.addBox(
                legs,
                [0.38, 0.19, 0.62],
                [side * 0.39, 0.16, 0.83],
                this.materials.shoe);
            shoe.rotation.y = side * 0.035;
            shoe.rotation.x = -0.03;
        });
        man.add(legs);
    }

    buildHead(man) {
        const head = new THREE.Group();
        head.position.set(0, 2.67, 0.08);

        const neck = this.addCylinder(head, 0.2, 0.23, 0.35, [0, -0.43, 0], this.materials.skin, 24);
        neck.rotation.x = 0.04;
        const face = this.mesh(
            new THREE.SphereGeometry(0.49, 36, 24),
            this.materials.skinLight,
            [0, 0, 0]);
        face.scale.set(1.02, 1.13, 0.92);
        head.add(face);

        [-1, 1].forEach(side => {
            const ear = this.mesh(
                new THREE.SphereGeometry(0.105, 18, 12),
                this.materials.skin,
                [side * 0.48, -0.02, 0]);
            ear.scale.set(0.68, 1.18, 0.58);
            head.add(ear);
        });

        const beard = this.mesh(
            new THREE.SphereGeometry(0.42, 30, 18),
            this.materials.stubble,
            [0, -0.18, 0.09]);
        beard.scale.set(0.92, 0.62, 0.9);
        head.add(beard);

        const capDome = this.mesh(
            new THREE.SphereGeometry(0.51, 32, 18, 0, Math.PI * 2, 0, Math.PI / 2),
            this.materials.cap,
            [0, 0.35, -0.015]);
        capDome.scale.set(1.06, 0.62, 1.02);
        head.add(capDome);
        const brim = this.mesh(
            new THREE.CylinderGeometry(0.31, 0.38, 0.075, 28),
            this.materials.capDark,
            [0, 0.31, 0.32]);
        brim.rotation.x = Math.PI / 2;
        brim.scale.z = 0.42;
        head.add(brim);

        const eyes = new THREE.Group();
        [-1, 1].forEach(side => {
            const white = this.mesh(
                new THREE.SphereGeometry(0.085, 20, 14),
                this.materials.eyeWhite,
                [side * 0.18, 0.08, 0.43]);
            white.scale.set(1.15, 0.72, 0.42);
            eyes.add(white);
            const iris = this.mesh(
                new THREE.SphereGeometry(0.038, 16, 12),
                this.materials.iris,
                [side * 0.18, 0.075, 0.49]);
            iris.scale.z = 0.5;
            eyes.add(iris);

            const brow = this.addBox(
                head,
                [0.22, 0.045, 0.045],
                [side * 0.18, 0.215, 0.43],
                this.materials.stubble);
            brow.rotation.z = side * 0.08;
        });
        head.add(eyes);

        const nose = this.mesh(
            new THREE.SphereGeometry(0.12, 22, 14),
            this.materials.cheek,
            [0, -0.06, 0.49]);
        nose.scale.set(0.76, 1.22, 0.82);
        head.add(nose);
        [-1, 1].forEach(side => {
            const cheek = this.mesh(
                new THREE.SphereGeometry(0.12, 18, 12),
                this.materials.cheek,
                [side * 0.28, -0.13, 0.42]);
            cheek.scale.set(1.18, 0.56, 0.32);
            head.add(cheek);
        });

        const mouth = this.mesh(
            new THREE.SphereGeometry(0.135, 24, 16),
            this.materials.mouth,
            [0, -0.31, 0.46]);
        mouth.scale.set(1, 0.14, 0.45);
        head.add(mouth);
        const tooth = this.addBox(
            head,
            [0.06, 0.025, 0.012],
            [-0.038, -0.285, 0.582],
            this.materials.tooth,
            false);
        tooth.visible = false;

        head.userData = { eyes, mouth, tooth };
        man.add(head);
        return head;
    }

    buildBag() {
        const bag = new THREE.Group();
        const plastic = new THREE.MeshPhysicalMaterial({
            color: 0xdeddd5,
            transparent: true,
            opacity: 0.72,
            roughness: 0.72,
            transmission: 0.08,
            side: THREE.DoubleSide
        });
        const sack = this.mesh(
            new THREE.SphereGeometry(0.34, 24, 16),
            plastic,
            [0, 0.32, 0]);
        sack.scale.set(0.9, 1.2, 0.66);
        bag.add(sack);
        [-0.12, 0.1].forEach((x, index) => {
            const can = this.addCylinder(
                bag,
                0.07,
                0.07,
                0.26,
                [x, 0.36, 0],
                index ? this.materials.canSilver : this.materials.can,
                18);
            can.rotation.z = index ? 0.18 : -0.12;
        });
        this.addBone(bag, [-0.2, 0.56, 0], [-0.08, 0.78, 0], 0.018, plastic);
        this.addBone(bag, [0.2, 0.56, 0], [0.08, 0.78, 0], 0.018, plastic);
        bag.position.set(1.42, 0, 0.46);
        this.scene.add(bag);
        return bag;
    }

    buildPigeons() {
        const pigeonMaterial = new THREE.MeshStandardMaterial({ color: 0x454b50, roughness: 0.9 });
        [[-3.25, 0.16, 1.1], [3.1, 0.16, -0.2]].forEach(([x, y, z], index) => {
            const pigeon = new THREE.Group();
            const body = this.mesh(
                new THREE.SphereGeometry(0.16, 18, 12),
                pigeonMaterial,
                [0, 0.1, 0]);
            body.scale.set(1.3, 0.8, 0.82);
            pigeon.add(body);
            const head = this.mesh(
                new THREE.SphereGeometry(0.09, 16, 10),
                pigeonMaterial,
                [0.14, 0.2, 0]);
            pigeon.add(head);
            const beak = this.mesh(
                new THREE.ConeGeometry(0.035, 0.12, 10),
                this.materials.can,
                [0.25, 0.19, 0]);
            beak.rotation.z = -Math.PI / 2;
            pigeon.add(beak);
            pigeon.position.set(x, y, z);
            pigeon.rotation.y = index ? 2.7 : 0.25;
            pigeon.userData.offset = index * 1.8;
            this.scene.add(pigeon);
            if (!this.pigeons) {
                this.pigeons = [];
            }
            this.pigeons.push(pigeon);
        });
    }

    buildDrizzle() {
        const geometry = new THREE.BufferGeometry();
        const positions = new Float32Array(150 * 3);
        for (let index = 0; index < 150; index++) {
            positions[index * 3] = (Math.random() - 0.5) * 16;
            positions[index * 3 + 1] = 0.4 + Math.random() * 6;
            positions[index * 3 + 2] = -7 + Math.random() * 12;
        }
        geometry.setAttribute("position", new THREE.BufferAttribute(positions, 3));
        this.drizzle = new THREE.Points(
            geometry,
            new THREE.PointsMaterial({
                color: 0xd3dde1,
                size: 0.018,
                transparent: true,
                opacity: 0.34,
                depthWrite: false
            }));
        this.scene.add(this.drizzle);
    }

    buildLighting() {
        const hemisphere = new THREE.HemisphereLight(0xd5dce0, 0x444944, 1.65);
        this.scene.add(hemisphere);

        this.keyLight = new THREE.DirectionalLight(0xe5ebed, 3.4);
        this.keyLight.position.set(-4.5, 8, 5);
        this.keyLight.castShadow = true;
        this.keyLight.shadow.mapSize.set(1536, 1536);
        this.keyLight.shadow.camera.left = -7;
        this.keyLight.shadow.camera.right = 7;
        this.keyLight.shadow.camera.top = 7;
        this.keyLight.shadow.camera.bottom = -3;
        this.scene.add(this.keyLight);

        this.fillLight = new THREE.PointLight(0xe4b36c, 18, 8, 2);
        this.fillLight.position.set(3.4, 3.2, 2.2);
        this.scene.add(this.fillLight);
    }

    bindEvents() {
        this.handlePointerMove = event => {
            const bounds = this.host.getBoundingClientRect();
            if (!bounds.width || !bounds.height) {
                return;
            }
            this.pointerLook.x = ((event.clientX - bounds.left) / bounds.width - 0.5) * 2;
            this.pointerLook.y = ((event.clientY - bounds.top) / bounds.height - 0.5) * 2;
        };
        this.handlePointerLeave = () => this.pointerLook.set(0, 0);
        this.host.addEventListener("pointermove", this.handlePointerMove);
        this.host.addEventListener("pointerleave", this.handlePointerLeave);
        this.resizeObserver = new ResizeObserver(() => this.resize());
        this.resizeObserver.observe(this.host);
    }

    resize() {
        const width = Math.max(1, this.host.clientWidth);
        const height = Math.max(1, this.host.clientHeight);
        this.camera.aspect = width / height;
        this.camera.updateProjectionMatrix();
        this.renderer.setSize(width, height, false);
        this.renderer.setPixelRatio(Math.min(window.devicePixelRatio, 1.8));
    }

    setActive(active) {
        this.active = active;
        if (active) {
            this.clock.getDelta();
            this.resize();
        }
    }

    setTalking(talking) {
        this.talking = talking;
        if (!talking) {
            this.setMouthPose("closed");
        }
    }

    setMouthPose(pose) {
        const { mouth, tooth } = this.mietek.userData;
        const values = {
            closed: [1, 0.14, 0.45],
            narrow: [0.82, 0.52, 0.48],
            round: [0.66, 0.78, 0.66],
            open: [0.96, 0.68, 0.52]
        }[pose] || [1, 0.14, 0.45];
        mouth.scale.set(values[0], values[1], values[2]);
        tooth.visible = pose === "open" || pose === "narrow";
    }

    cancel({ reset = false } = {}) {
        this.setTalking(false);
        this.pointerLook.set(0, 0);
        if (reset) {
            this.mietek.rotation.set(0, 0, 0);
            this.mietek.userData.head.rotation.set(0, 0, 0);
            this.mietek.userData.rightArm.rotation.copy(this.mietek.userData.rightArmRest);
        }
    }

    animate() {
        this.animationFrame = requestAnimationFrame(() => this.animate());
        if (!this.active || this.sceneElement.hidden) {
            this.clock.getDelta();
            return;
        }

        const delta = this.clock.getDelta();
        const elapsed = this.clock.elapsedTime;
        this.smoothLook.lerp(this.pointerLook, Math.min(1, delta * 2.5));
        this.camera.position.x = this.smoothLook.x * 0.2;
        this.camera.position.y = 2.65 - this.smoothLook.y * 0.08;
        this.camera.lookAt(
            this.cameraTarget.x + this.smoothLook.x * 0.22,
            this.cameraTarget.y - this.smoothLook.y * 0.1,
            this.cameraTarget.z);

        const { head, eyes, rightArm, rightArmRest } = this.mietek.userData;
        this.mietek.position.y = Math.sin(elapsed * 1.25) * 0.008;
        head.rotation.y =
            this.smoothLook.x * 0.08
            + Math.sin(elapsed * 0.43) * (this.talking ? 0.02 : 0.045);
        head.rotation.x =
            -this.smoothLook.y * 0.035
            + (this.talking ? Math.sin(elapsed * 4.2) * 0.025 : 0);
        head.rotation.z = this.talking ? Math.sin(elapsed * 2.8) * 0.018 : 0;

        const blink = Math.sin(elapsed * 2.45 + 0.7) > 0.993 ? 0.08 : 1;
        eyes.scale.y += (blink - eyes.scale.y) * 0.42;

        rightArm.rotation.x = rightArmRest.x
            + (this.talking ? -0.1 - Math.sin(elapsed * 2.25) * 0.08 : 0);
        rightArm.rotation.z = rightArmRest.z
            + (this.talking ? Math.sin(elapsed * 2.25) * 0.06 : 0);

        if (!reducedMotion) {
            const positions = this.drizzle.geometry.attributes.position;
            for (let index = 0; index < positions.count; index++) {
                let y = positions.getY(index) - delta * 0.42;
                if (y < 0.1) {
                    y = 5.8 + (index % 13) * 0.04;
                }
                positions.setY(index, y);
            }
            positions.needsUpdate = true;
            this.pigeons?.forEach(pigeon => {
                pigeon.rotation.x = Math.sin(elapsed * 1.2 + pigeon.userData.offset) * 0.025;
            });
        }

        this.fillLight.intensity = 17 + Math.sin(elapsed * 1.7) * 0.7;
        this.renderer.render(this.scene, this.camera);
    }
}

export function initializeMietekThreeScenes(root = document) {
    root.querySelectorAll("[data-avatar-scene='mietek']").forEach(sceneElement => {
        if (controllers.has(sceneElement)) {
            return;
        }
        const host = sceneElement.querySelector("[data-mietek-three-host]");
        if (!host) {
            return;
        }
        try {
            controllers.set(sceneElement, new MietekThreeController(sceneElement, host));
        } catch (error) {
            host.querySelector("[data-mietek-three-loading]")?.setAttribute("hidden", "");
            const fallback = host.querySelector("[data-mietek-three-fallback]");
            if (fallback) {
                fallback.hidden = false;
            }
            console.error("The Three.js Pan Mietek scene could not be initialized.", error);
        }
    });
}

export function getMietekThreeController(sceneElement) {
    return sceneElement ? controllers.get(sceneElement) || null : null;
}
