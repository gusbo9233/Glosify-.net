import * as THREE from "https://cdn.jsdelivr.net/npm/three@0.180.0/build/three.module.js";

const reducedMotion = window.matchMedia("(prefers-reduced-motion: reduce)").matches;
const controllers = new WeakMap();

const drinkCatalog = {
    lightBeer: {
        color: 0xd98a18,
        foam: 0xfff0c2,
        bubbles: true,
        scale: 1,
        source: "light",
        category: "beer"
    },
    darkBeer: {
        color: 0x4b1709,
        foam: 0xd8b98b,
        bubbles: true,
        scale: 1,
        source: "dark",
        category: "beer"
    },
    vodka: {
        color: 0xd9eef2,
        foam: null,
        bubbles: false,
        scale: 1,
        source: "bottle",
        category: "spirit"
    },
    redWine: {
        color: 0x74162a,
        foam: null,
        bubbles: false,
        scale: 1,
        source: "bottle",
        category: "wine"
    },
    sparklingWater: {
        color: 0xb9e2ee,
        foam: null,
        bubbles: true,
        scale: 0.82,
        source: "bottle",
        category: "nonAlcoholic"
    },
    stillWater: {
        color: 0xc9e1e7,
        foam: null,
        bubbles: false,
        scale: 0.82,
        source: "bottle",
        category: "nonAlcoholic"
    },
    appleJuice: {
        color: 0xd39a24,
        foam: null,
        bubbles: false,
        scale: 0.86,
        source: "bottle",
        category: "nonAlcoholic"
    }
};

const defaultDrink = drinkCatalog.lightBeer;

class BartenderThreeController {
    constructor(sceneElement, host) {
        this.sceneElement = sceneElement;
        this.host = host;
        this.generation = 0;
        this.active = !sceneElement.hidden;
        this.busy = false;
        this.talking = false;
        this.activeDrink = null;
        this.pointerLook = new THREE.Vector2();
        this.smoothLook = new THREE.Vector2();
        this.pointerRay = new THREE.Vector2();
        this.raycaster = new THREE.Raycaster();
        this.clock = new THREE.Clock();

        this.initializeRenderer();
        this.initializeScene();
        this.bindEvents();
        this.animate();

        requestAnimationFrame(() => {
            host.querySelector("[data-bartender-three-loading]")?.setAttribute("hidden", "");
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
        this.renderer.toneMappingExposure = 1.08;
        this.renderer.domElement.setAttribute("aria-hidden", "true");
        this.renderer.domElement.tabIndex = -1;
        this.host.prepend(this.renderer.domElement);
    }

    initializeScene() {
        this.scene = new THREE.Scene();
        this.scene.background = new THREE.Color(0x100b08);
        this.scene.fog = new THREE.FogExp2(0x120b07, 0.036);

        this.camera = new THREE.PerspectiveCamera(45, 1, 0.08, 40);
        this.camera.position.set(0, 2.15, 7.2);
        this.cameraTarget = new THREE.Vector3(0, 1.72, -0.2);

        this.materials = this.buildMaterials();
        this.room = this.buildRoom();
        this.taps = {
            light: this.buildTap(-2.55, 0x871d27),
            dark: this.buildTap(-1.82, 0x1e3a5c)
        };
        this.bottle = this.buildServiceBottle();
        this.bartender = this.buildBartender();
        this.drinkGlasses = {
            beer: this.buildDrinkGlass("beer"),
            spirit: this.buildDrinkGlass("spirit"),
            wine: this.buildDrinkGlass("wine"),
            nonAlcoholic: this.buildDrinkGlass("nonAlcoholic")
        };
        this.drinkGlass = this.drinkGlasses.beer;
        this.guestHand = this.buildGuestHand();
        this.paymentCard = this.buildPaymentCard();
        this.receipt = this.buildReceipt();
        this.polishSet = this.buildPolishSet();
        this.wipeCloth = this.buildWipeCloth();
        this.buildLighting();
        this.resetObjects();
        this.resize();
    }

    buildMaterials() {
        return {
            woodDark: new THREE.MeshStandardMaterial({ color: 0x2b170b, roughness: 0.68 }),
            wood: new THREE.MeshStandardMaterial({ color: 0x5a3116, roughness: 0.58 }),
            woodLight: new THREE.MeshStandardMaterial({ color: 0x875025, roughness: 0.48 }),
            brass: new THREE.MeshStandardMaterial({
                color: 0xc4943d,
                metalness: 0.75,
                roughness: 0.24
            }),
            blackMetal: new THREE.MeshStandardMaterial({
                color: 0x151515,
                metalness: 0.72,
                roughness: 0.35
            }),
            cream: new THREE.MeshStandardMaterial({ color: 0xe9ddbd, roughness: 0.8 }),
            red: new THREE.MeshStandardMaterial({ color: 0x871d27, roughness: 0.63 }),
            skin: new THREE.MeshStandardMaterial({ color: 0xc88856, roughness: 0.72 }),
            skinLight: new THREE.MeshStandardMaterial({ color: 0xdca06a, roughness: 0.72 }),
            nail: new THREE.MeshStandardMaterial({ color: 0xe2ad84, roughness: 0.55 }),
            shirt: new THREE.MeshStandardMaterial({ color: 0xe5dbc5, roughness: 0.85 }),
            vest: new THREE.MeshStandardMaterial({ color: 0x31241b, roughness: 0.82 }),
            glass: new THREE.MeshPhysicalMaterial({
                color: 0xddebf2,
                transparent: true,
                opacity: 0.26,
                roughness: 0.06,
                transmission: 0.5,
                thickness: 0.08,
                metalness: 0,
                side: THREE.DoubleSide
            })
        };
    }

    mesh(geometry, material, position, castShadow = true, receiveShadow = true) {
        const object = new THREE.Mesh(geometry, material);
        object.position.set(position[0], position[1], position[2]);
        object.castShadow = castShadow;
        object.receiveShadow = receiveShadow;
        return object;
    }

    addBox(parent, size, position, material, castShadow = true) {
        const object = this.mesh(
            new THREE.BoxGeometry(size[0], size[1], size[2]),
            material,
            position,
            castShadow,
            true);
        parent.add(object);
        return object;
    }

    addCylinder(
        parent,
        radiusTop,
        radiusBottom,
        height,
        position,
        material,
        radialSegments = 24) {
        const object = this.mesh(
            new THREE.CylinderGeometry(
                radiusTop,
                radiusBottom,
                height,
                radialSegments),
            material,
            position);
        parent.add(object);
        return object;
    }

    makeCanvasTexture(draw, width = 1024, height = 256) {
        const canvas = document.createElement("canvas");
        canvas.width = width;
        canvas.height = height;
        const context = canvas.getContext("2d");
        draw(context, width, height);
        const texture = new THREE.CanvasTexture(canvas);
        texture.colorSpace = THREE.SRGBColorSpace;
        texture.anisotropy = this.renderer.capabilities.getMaxAnisotropy();
        return texture;
    }

    makeSignTexture() {
        return this.makeCanvasTexture((context, width, height) => {
            context.fillStyle = "#21130b";
            context.fillRect(0, 0, width, height);
            context.strokeStyle = "#b88a3e";
            context.lineWidth = 10;
            context.strokeRect(14, 14, width - 28, height - 28);
            context.textAlign = "center";
            context.fillStyle = "#f4dfad";
            context.font = "700 72px Georgia";
            context.fillText("POD BIAŁYM ORŁEM", width / 2, 112);
            context.fillStyle = "#c49b5f";
            context.font = "28px Georgia";
            context.fillText("PIWO • WÓDKA • GOŚCINNOŚĆ", width / 2, 172);
            context.fillStyle = "#8e2029";
            context.fillRect(width / 2 - 180, 196, 360, 5);
        });
    }

    makeBottleLabel(text, accent = "#8c1d28") {
        return this.makeCanvasTexture((context, width, height) => {
            context.fillStyle = "#efe2c2";
            context.fillRect(0, 0, width, height);
            context.fillStyle = accent;
            context.fillRect(0, 0, width, 38);
            context.fillStyle = "#482819";
            context.textAlign = "center";
            context.font = "700 42px Georgia";
            context.fillText(text, width / 2, 118);
            context.font = "24px Georgia";
            context.fillText("POLSKA", width / 2, 164);
        }, 320, 200);
    }

    buildRoom() {
        const { woodDark, wood, woodLight, brass, blackMetal, cream, red } = this.materials;
        const room = new THREE.Group();
        this.scene.add(room);

        const floorMaterial = new THREE.MeshStandardMaterial({
            color: 0x24150d,
            roughness: 0.86
        });
        const floor = this.mesh(
            new THREE.PlaneGeometry(18, 20),
            floorMaterial,
            [0, 0, 1],
            false,
            true);
        floor.rotation.x = -Math.PI / 2;
        room.add(floor);

        const wallMaterial = new THREE.MeshStandardMaterial({
            color: 0x28170e,
            roughness: 0.95
        });
        this.addBox(room, [14, 6, 0.25], [0, 3, -4.7], wallMaterial, false);
        for (let x = -6.5; x <= 6.5; x += 1.05) {
            this.addBox(room, [0.035, 2.35, 0.06], [x, 1.18, -4.52], woodDark, false);
        }

        const shelfBack = new THREE.MeshStandardMaterial({
            color: 0x1c100a,
            roughness: 0.88
        });
        this.addBox(room, [7.7, 2.35, 0.24], [0, 2.92, -4.34], shelfBack, false);
        for (const y of [2.08, 3.05, 4.02]) {
            this.addBox(room, [8.15, 0.16, 0.62], [0, y, -4.02], woodLight);
            this.addBox(room, [8.26, 0.07, 0.66], [0, y + 0.1, -3.99], brass);
        }

        const bottleColors = [
            0x8d2c1c,
            0x275a36,
            0xd4dce0,
            0x8a5424,
            0x263b58,
            0xb98723,
            0x562137
        ];
        const labelNames = ["ŻYTNIA", "MIÓD", "CZYSTA", "SOK"];
        for (let row = 0; row < 2; row++) {
            for (let index = 0; index < 13; index++) {
                const bottle = new THREE.Group();
                const color = bottleColors[(index + row * 2) % bottleColors.length];
                const bottleMaterial = new THREE.MeshPhysicalMaterial({
                    color,
                    roughness: 0.18,
                    transmission: color === 0xd4dce0 ? 0.35 : 0.02,
                    transparent: true,
                    opacity: color === 0xd4dce0 ? 0.72 : 0.9
                });
                const height = 0.54 + ((index * 7 + row * 3) % 5) * 0.045;
                this.addCylinder(
                    bottle,
                    0.11,
                    0.13,
                    height,
                    [0, height / 2, 0],
                    bottleMaterial,
                    12);
                this.addCylinder(
                    bottle,
                    0.055,
                    0.07,
                    0.18,
                    [0, height + 0.07, 0],
                    bottleMaterial,
                    12);
                this.addCylinder(
                    bottle,
                    0.06,
                    0.06,
                    0.04,
                    [0, height + 0.17, 0],
                    blackMetal,
                    12);
                if (index % 3 === 0) {
                    const label = new THREE.Mesh(
                        new THREE.PlaneGeometry(0.2, 0.14),
                        new THREE.MeshBasicMaterial({
                            map: this.makeBottleLabel(
                                labelNames[(Math.floor(index / 3) + row) % labelNames.length]),
                            transparent: true
                        }));
                    label.position.set(0, height * 0.5, 0.135);
                    bottle.add(label);
                }
                bottle.position.set(
                    -3.55 + index * 0.59,
                    row === 0 ? 2.2 : 3.17,
                    -3.67);
                room.add(bottle);
            }
        }

        const signTexture = this.makeSignTexture();
        const sign = new THREE.Mesh(
            new THREE.PlaneGeometry(4.5, 1.12),
            new THREE.MeshStandardMaterial({
                map: signTexture,
                emissiveMap: signTexture,
                emissive: 0x4d250e,
                emissiveIntensity: 0.42,
                roughness: 0.5
            }));
        sign.position.set(0, 5, -4.5);
        room.add(sign);

        for (let index = 0; index < 13; index++) {
            const triangle = new THREE.Mesh(
                new THREE.ConeGeometry(0.14, 0.42, 3),
                index % 2 === 0 ? cream : red);
            triangle.rotation.z = Math.PI;
            triangle.position.set(
                -4.2 + index * 0.7,
                4.43 - Math.abs(6 - index) * 0.035,
                -4.38);
            room.add(triangle);
        }

        const counter = new THREE.Group();
        this.addBox(counter, [10.8, 1.75, 1.5], [0, 0.28, 1.15], wood);
        this.addBox(counter, [11.1, 0.24, 1.78], [0, 1.2, 1.15], woodLight);
        this.addBox(counter, [11.1, 0.06, 1.86], [0, 1.35, 1.15], brass);
        this.addBox(counter, [10.3, 0.12, 0.12], [0, 0.68, 2.02], brass);
        room.add(counter);

        const snackBowl = new THREE.Group();
        const bowl = this.mesh(
            new THREE.CylinderGeometry(0.46, 0.3, 0.18, 28, 1, true),
            new THREE.MeshStandardMaterial({
                color: 0x71351b,
                roughness: 0.7,
                side: THREE.DoubleSide
            }),
            [0, 0, 0]);
        snackBowl.add(bowl);
        const snackMaterial = new THREE.MeshStandardMaterial({
            color: 0xd49a42,
            roughness: 0.9
        });
        const sticks = [];
        for (let index = 0; index < 12; index++) {
            const stick = this.addCylinder(
                snackBowl,
                0.018,
                0.018,
                0.65,
                [-0.2 + (index % 6) * 0.07, 0.3, -0.07 + Math.floor(index / 6) * 0.09],
                snackMaterial,
                7);
            stick.rotation.z = -0.35 + (index % 5) * 0.14;
            sticks.push(stick);
        }
        snackBowl.position.set(-3.75, 1.51, 2.18);
        snackBowl.userData.sticks = sticks;
        room.add(snackBowl);
        room.userData.snackBowl = snackBowl;

        const snackBoard = new THREE.Group();
        const board = this.addBox(
            snackBoard,
            [1.65, 0.08, 0.65],
            [0, 0, 0],
            woodLight);
        board.rotation.y = -0.08;
        const pickleMaterial = new THREE.MeshStandardMaterial({
            color: 0x627c35,
            roughness: 0.93
        });
        const sausageMaterial = new THREE.MeshStandardMaterial({
            color: 0x8e3926,
            roughness: 0.72
        });
        for (let index = 0; index < 3; index++) {
            const pickle = this.addCylinder(
                snackBoard,
                0.06,
                0.065,
                0.38,
                [-0.48 + index * 0.19, 0.12, 0],
                pickleMaterial,
                12);
            pickle.rotation.z = Math.PI / 2;
            const sausage = this.mesh(
                new THREE.CapsuleGeometry(0.07, 0.28, 5, 12),
                sausageMaterial,
                [0.28 + index * 0.2, 0.13, 0]);
            sausage.rotation.z = Math.PI / 2;
            snackBoard.add(sausage);
        }
        snackBoard.position.set(3.35, 1.45, 1.62);
        room.add(snackBoard);

        return room;
    }

    buildTap(x, badgeColor) {
        const { brass, blackMetal } = this.materials;
        const tap = new THREE.Group();
        this.addCylinder(tap, 0.12, 0.16, 0.9, [0, 0.45, 0], brass, 24);
        this.addCylinder(tap, 0.21, 0.21, 0.1, [0, 0.93, 0], brass, 24);
        const badge = this.addCylinder(
            tap,
            0.24,
            0.24,
            0.12,
            [0, 1.12, 0],
            new THREE.MeshStandardMaterial({ color: badgeColor, roughness: 0.6 }),
            28);
        badge.rotation.x = Math.PI / 2;
        const handle = this.addBox(tap, [0.12, 0.48, 0.12], [0, 1.38, 0], blackMetal);
        const spout = this.addCylinder(tap, 0.055, 0.065, 0.52, [0, 0.68, 0.24], brass, 14);
        spout.rotation.x = Math.PI / 2;
        this.addCylinder(tap, 0.045, 0.05, 0.26, [0, 0.44, 0.5], brass, 14);
        tap.position.set(x, 1.34, 0.94);
        this.scene.add(tap);

        const stream = this.mesh(
            new THREE.CylinderGeometry(0.028, 0.04, 0.54, 10),
            new THREE.MeshPhysicalMaterial({
                color: 0xd99017,
                transparent: true,
                opacity: 0.72,
                roughness: 0.2,
                transmission: 0.1
            }),
            [x, 1.89, 1.43],
            false,
            false);
        stream.visible = false;
        this.scene.add(stream);
        return { tap, handle, stream, x };
    }

    buildServiceBottle() {
        const bottle = new THREE.Group();
        const bottleMaterial = new THREE.MeshPhysicalMaterial({
            color: 0xd9eef2,
            roughness: 0.16,
            transmission: 0.42,
            transparent: true,
            opacity: 0.78
        });
        this.addCylinder(bottle, 0.15, 0.19, 0.72, [0, 0.36, 0], bottleMaterial, 18);
        this.addCylinder(bottle, 0.07, 0.1, 0.27, [0, 0.84, 0], bottleMaterial, 16);
        this.addCylinder(
            bottle,
            0.075,
            0.075,
            0.06,
            [0, 1.005, 0],
            this.materials.red,
            16);
        const label = new THREE.Mesh(
            new THREE.PlaneGeometry(0.25, 0.2),
            new THREE.MeshBasicMaterial({
                map: this.makeBottleLabel("CZYSTA"),
                transparent: true
            }));
        label.position.set(0, 0.45, 0.185);
        bottle.add(label);
        bottle.position.set(2.05, 1.37, 1.45);
        this.scene.add(bottle);

        const stream = this.mesh(
            new THREE.CylinderGeometry(0.025, 0.035, 0.5, 10),
            new THREE.MeshPhysicalMaterial({
                color: 0xd9eef2,
                transparent: true,
                opacity: 0.65,
                roughness: 0.12
            }),
            [1.45, 2.03, 1.58],
            false,
            false);
        stream.visible = false;
        this.scene.add(stream);
        bottle.userData.stream = stream;
        return bottle;
    }

    createBartenderHand(side) {
        const { skinLight, skin, nail } = this.materials;
        const hand = new THREE.Group();
        const palm = this.mesh(
            new THREE.CapsuleGeometry(0.105, 0.15, 6, 14),
            skinLight,
            [0, -0.035, 0]);
        palm.scale.set(0.92, 1, 0.58);
        hand.add(palm);

        const knuckleMaterial = new THREE.MeshStandardMaterial({
            color: 0xc98b5d,
            roughness: 0.8
        });
        const fingerRoots = [];
        const fingerLengths = [0.145, 0.165, 0.155, 0.13];
        for (let index = 0; index < 4; index++) {
            const root = new THREE.Group();
            root.position.set(
                -0.072 + index * 0.048,
                -0.115 - Math.abs(1.5 - index) * 0.008,
                0.02);
            hand.add(root);

            const proximalLength = fingerLengths[index];
            const proximal = this.mesh(
                new THREE.CapsuleGeometry(0.026, proximalLength, 4, 9),
                skinLight,
                [0, -proximalLength * 0.46, 0]);
            proximal.scale.z = 0.82;
            root.add(proximal);

            const knuckle = this.mesh(
                new THREE.SphereGeometry(0.032, 10, 8),
                knuckleMaterial,
                [0, -proximalLength * 0.92, 0]);
            knuckle.scale.z = 0.76;
            root.add(knuckle);

            const middle = new THREE.Group();
            middle.position.set(0, -proximalLength * 0.91, 0);
            root.add(middle);
            const distalLength = proximalLength * 0.58;
            const distal = this.mesh(
                new THREE.CapsuleGeometry(0.022, distalLength, 4, 9),
                skinLight,
                [0, -distalLength * 0.42, 0]);
            distal.scale.z = 0.78;
            middle.add(distal);
            const fingernail = this.mesh(
                new THREE.SphereGeometry(0.017, 8, 6),
                nail,
                [0, -distalLength * 0.72, 0.019]);
            fingernail.scale.set(0.78, 0.5, 0.22);
            middle.add(fingernail);
            fingerRoots.push({ root, middle });
        }

        const thumbRoot = new THREE.Group();
        thumbRoot.position.set(side * 0.095, -0.02, 0.035);
        thumbRoot.rotation.z = side * -0.9;
        hand.add(thumbRoot);
        const thumb = this.mesh(
            new THREE.CapsuleGeometry(0.03, 0.12, 5, 10),
            skinLight,
            [0, -0.07, 0]);
        thumb.scale.z = 0.84;
        thumbRoot.add(thumb);
        const thumbTip = new THREE.Group();
        thumbTip.position.set(0, -0.13, 0);
        thumbRoot.add(thumbTip);
        thumbTip.add(this.mesh(
            new THREE.CapsuleGeometry(0.026, 0.07, 4, 9),
            skinLight,
            [0, -0.035, 0]));

        hand.userData.setGrip = amount => {
            const grip = THREE.MathUtils.clamp(amount, 0, 1);
            fingerRoots.forEach(({ root, middle }, index) => {
                root.rotation.x = -0.08 - grip * (0.58 + index * 0.035);
                middle.rotation.x = -0.12 - grip * 0.9;
                root.rotation.z = (index - 1.5) * 0.035;
            });
            thumbRoot.rotation.x = -0.12 - grip * 0.62;
            thumbTip.rotation.x = -0.08 - grip * 0.72;
        };
        hand.userData.setGrip(0.18);
        return hand;
    }

    createArm(side) {
        const arm = new THREE.Group();
        const upper = this.addCylinder(
            arm,
            0.14,
            0.15,
            0.62,
            [0, -0.28, 0],
            this.materials.shirt,
            18);
        upper.rotation.z = side * 0.12;
        const elbow = new THREE.Group();
        elbow.position.set(side * 0.04, -0.58, 0);
        arm.add(elbow);
        this.addCylinder(
            elbow,
            0.115,
            0.13,
            0.58,
            [0, -0.27, 0.05],
            this.materials.skin,
            18);
        const wrist = this.addCylinder(
            elbow,
            0.075,
            0.1,
            0.16,
            [0, -0.58, 0.06],
            this.materials.skin,
            14);
        wrist.scale.z = 0.84;
        const hand = this.createBartenderHand(side);
        hand.position.set(0, -0.69, 0.07);
        elbow.add(hand);
        arm.userData.elbow = elbow;
        arm.userData.hand = hand;
        return arm;
    }

    buildBartender() {
        const { skin, skinLight, shirt, vest, brass } = this.materials;
        const bartender = new THREE.Group();
        bartender.position.set(0, 1.35, -0.72);
        this.scene.add(bartender);

        const legs = new THREE.Group();
        this.addCylinder(legs, 0.27, 0.29, 1.25, [-0.28, -0.45, 0], vest, 20);
        this.addCylinder(legs, 0.27, 0.29, 1.25, [0.28, -0.45, 0], vest, 20);
        bartender.add(legs);

        const torso = this.mesh(
            new THREE.CapsuleGeometry(0.58, 0.78, 7, 18),
            shirt,
            [0, 0.78, 0]);
        torso.scale.set(1.06, 1, 0.62);
        bartender.add(torso);
        const vestFront = this.mesh(
            new THREE.BoxGeometry(0.94, 1.05, 0.16),
            vest,
            [0, 0.72, 0.39]);
        vestFront.rotation.x = -0.05;
        bartender.add(vestFront);
        for (const y of [0.4, 0.7, 1]) {
            bartender.add(this.mesh(new THREE.SphereGeometry(0.035, 10, 8), brass, [0, y, 0.49]));
        }
        const collarLeft = this.mesh(
            new THREE.BoxGeometry(0.34, 0.4, 0.09),
            shirt,
            [-0.17, 1.3, 0.42]);
        collarLeft.rotation.z = -0.62;
        bartender.add(collarLeft);
        const collarRight = collarLeft.clone();
        collarRight.position.x = 0.17;
        collarRight.rotation.z = 0.62;
        bartender.add(collarRight);
        this.addCylinder(bartender, 0.19, 0.22, 0.32, [0, 1.55, 0], skin, 20);

        const head = new THREE.Group();
        head.position.set(0, 2.05, 0);
        bartender.add(head);
        const face = this.mesh(
            new THREE.SphereGeometry(0.48, 32, 24),
            skinLight,
            [0, 0, 0]);
        face.scale.set(0.85, 1.1, 0.83);
        head.add(face);
        const earLeft = this.mesh(
            new THREE.SphereGeometry(0.12, 16, 12),
            skin,
            [-0.43, 0, 0]);
        earLeft.scale.set(0.6, 1.05, 0.5);
        head.add(earLeft);
        const earRight = earLeft.clone();
        earRight.position.x = 0.43;
        head.add(earRight);

        const hairMaterial = new THREE.MeshStandardMaterial({
            color: 0x50453c,
            roughness: 0.95
        });
        const hair = this.mesh(
            new THREE.SphereGeometry(0.45, 28, 18, 0, Math.PI * 2, 0, Math.PI * 0.48),
            hairMaterial,
            [0, 0.18, -0.02]);
        hair.scale.set(0.9, 0.95, 0.86);
        head.add(hair);

        const eyeWhite = new THREE.MeshStandardMaterial({ color: 0xf2eadb, roughness: 0.5 });
        const pupilMaterial = new THREE.MeshStandardMaterial({ color: 0x2b1b10, roughness: 0.6 });
        const eyes = new THREE.Group();
        for (const x of [-0.17, 0.17]) {
            const eye = this.mesh(
                new THREE.SphereGeometry(0.075, 16, 10),
                eyeWhite,
                [x, 0.07, 0.39]);
            eye.scale.set(1, 0.66, 0.45);
            eyes.add(eye);
            const pupil = this.mesh(
                new THREE.SphereGeometry(0.032, 12, 8),
                pupilMaterial,
                [x, 0.07, 0.438]);
            pupil.scale.set(1, 1.2, 0.4);
            eyes.add(pupil);
        }
        head.add(eyes);
        for (const x of [-0.17, 0.17]) {
            const brow = this.addBox(
                head,
                [0.18, 0.035, 0.03],
                [x, 0.19, 0.415],
                new THREE.MeshStandardMaterial({ color: 0x423328, roughness: 0.9 }));
            brow.rotation.z = x < 0 ? -0.08 : 0.08;
        }
        const nose = this.mesh(
            new THREE.ConeGeometry(0.065, 0.24, 16),
            skin,
            [0, -0.04, 0.48]);
        nose.rotation.x = Math.PI / 2;
        head.add(nose);

        const mustacheMaterial = new THREE.MeshStandardMaterial({
            color: 0x3d2d23,
            roughness: 0.95
        });
        for (const side of [-1, 1]) {
            const half = this.mesh(
                new THREE.CapsuleGeometry(0.045, 0.23, 4, 12),
                mustacheMaterial,
                [side * 0.11, -0.18, 0.45]);
            half.rotation.z = side * (Math.PI / 2 + 0.25);
            half.scale.set(1, 1, 0.45);
            head.add(half);
        }
        const mouth = this.mesh(
            new THREE.SphereGeometry(0.09, 16, 10),
            new THREE.MeshStandardMaterial({ color: 0x743c30, roughness: 0.8 }),
            [0, -0.28, 0.43]);
        mouth.scale.set(1, 0.18, 0.24);
        head.add(mouth);

        const leftArm = this.createArm(-1);
        leftArm.position.set(-0.66, 1.18, 0);
        leftArm.rotation.z = -0.14;
        bartender.add(leftArm);
        const rightArm = this.createArm(1);
        rightArm.position.set(0.66, 1.18, 0);
        rightArm.rotation.z = 0.14;
        bartender.add(rightArm);

        bartender.userData = {
            head,
            eyes,
            mouth,
            leftArm,
            rightArm,
            leftElbow: leftArm.userData.elbow,
            rightElbow: rightArm.userData.elbow,
            leftHand: leftArm.userData.hand,
            rightHand: rightArm.userData.hand
        };
        return bartender;
    }

    buildDrinkGlass(glassware) {
        const glass = new THREE.Group();
        glass.visible = false;
        glass.position.set(-2.55, 1.37, 1.46);

        const dimensions = {
            beer: {
                top: 0.26,
                bottom: 0.21,
                height: 0.72,
                liquidTop: 0.235,
                liquidBottom: 0.19,
                liquidHeight: 0.59,
                fillBase: 0.055
            },
            spirit: {
                top: 0.18,
                bottom: 0.13,
                height: 0.34,
                liquidTop: 0.15,
                liquidBottom: 0.11,
                liquidHeight: 0.25,
                fillBase: 0.045
            },
            wine: {
                top: 0.26,
                bottom: 0.1,
                height: 0.4,
                liquidTop: 0.225,
                liquidBottom: 0.085,
                liquidHeight: 0.3,
                fillBase: 0.34
            },
            nonAlcoholic: {
                top: 0.23,
                bottom: 0.19,
                height: 0.58,
                liquidTop: 0.205,
                liquidBottom: 0.17,
                liquidHeight: 0.46,
                fillBase: 0.05
            }
        }[glassware];
        const shellCenter = glassware === "wine"
            ? dimensions.fillBase + dimensions.height / 2
            : dimensions.height / 2;

        const shell = this.mesh(
            new THREE.CylinderGeometry(
                dimensions.top,
                dimensions.bottom,
                dimensions.height,
                32,
                1,
                true),
            this.materials.glass,
            [0, shellCenter, 0]);
        glass.add(shell);
        if (glassware === "wine") {
            this.addCylinder(
                glass,
                0.028,
                0.028,
                0.3,
                [0, 0.19, 0],
                this.materials.glass,
                16);
            this.addCylinder(
                glass,
                0.17,
                0.17,
                0.025,
                [0, 0.025, 0],
                this.materials.glass,
                32);
        } else {
            this.addCylinder(
                glass,
                dimensions.bottom,
                dimensions.bottom,
                0.045,
                [0, 0.025, 0],
                this.materials.glass,
                32);
        }
        const rimY = shellCenter + dimensions.height / 2;
        const rim = this.mesh(
            new THREE.TorusGeometry(dimensions.top, 0.016, 10, 32),
            new THREE.MeshPhysicalMaterial({
                color: 0xebf6fa,
                transparent: true,
                opacity: 0.55,
                roughness: 0.08
            }),
            [0, rimY, 0]);
        rim.rotation.x = Math.PI / 2;
        glass.add(rim);

        const liquidMaterial = new THREE.MeshPhysicalMaterial({
            color: defaultDrink.color,
            transparent: true,
            opacity: 0.86,
            roughness: 0.18,
            transmission: 0.06
        });
        const fill = new THREE.Group();
        fill.position.y = dimensions.fillBase;
        const liquidGeometry = new THREE.CylinderGeometry(
            dimensions.liquidTop,
            dimensions.liquidBottom,
            dimensions.liquidHeight,
            28);
        liquidGeometry.translate(0, dimensions.liquidHeight / 2, 0);
        const liquid = this.mesh(liquidGeometry, liquidMaterial, [0, 0, 0], false, false);
        fill.add(liquid);
        fill.scale.y = 0.001;
        glass.add(fill);

        const foam = this.addCylinder(
            glass,
            dimensions.liquidTop,
            dimensions.liquidTop * 0.97,
            0.065,
            [0, dimensions.fillBase + dimensions.liquidHeight, 0],
            new THREE.MeshStandardMaterial({
                color: defaultDrink.foam,
                roughness: 0.82,
                transparent: true,
                opacity: 0.96
            }),
            28);
        foam.visible = false;

        const bubbles = [];
        const bubbleMaterial = new THREE.MeshBasicMaterial({
            color: 0xffd984,
            transparent: true,
            opacity: 0.68
        });
        for (let index = 0; index < 9; index++) {
            const bubble = this.mesh(
                new THREE.SphereGeometry(0.012 + (index % 3) * 0.004, 8, 6),
                bubbleMaterial,
                [
                    -dimensions.liquidTop * 0.55
                        + (index * 0.083) % (dimensions.liquidTop * 1.1),
                    dimensions.fillBase
                        + 0.04
                        + (index * 0.071) % (dimensions.liquidHeight * 0.8),
                    -0.04 + (index % 2) * 0.08
                ],
                false,
                false);
            bubble.userData.offset = index * 0.37;
            bubble.visible = false;
            glass.add(bubble);
            bubbles.push(bubble);
        }
        glass.userData = {
            shell,
            fill,
            liquid,
            foam,
            bubbles,
            glassware,
            fillBase: dimensions.fillBase,
            fillHeight: dimensions.liquidHeight
        };
        this.scene.add(glass);
        return glass;
    }

    buildGuestHand() {
        const { skin, skinLight, nail } = this.materials;
        const hand = new THREE.Group();
        const sleeveMaterial = new THREE.MeshStandardMaterial({
            color: 0x263547,
            roughness: 0.88
        });
        const sleeve = this.addCylinder(
            hand,
            0.19,
            0.29,
            1.02,
            [0.35, -0.52, -0.04],
            sleeveMaterial,
            20);
        sleeve.rotation.z = -0.2;
        const cuff = this.addCylinder(
            hand,
            0.195,
            0.2,
            0.16,
            [0.16, -0.05, -0.01],
            sleeveMaterial,
            20);
        cuff.rotation.z = -0.12;

        const wrist = this.mesh(
            new THREE.CapsuleGeometry(0.105, 0.14, 5, 14),
            skin,
            [0.07, 0.05, 0.01]);
        wrist.rotation.z = -0.12;
        wrist.scale.z = 0.65;
        hand.add(wrist);
        const palm = this.mesh(
            new THREE.CapsuleGeometry(0.145, 0.25, 7, 18),
            skinLight,
            [0.15, 0.2, 0.03]);
        palm.rotation.z = -0.08;
        palm.scale.set(0.88, 1, 0.52);
        hand.add(palm);

        const fingerRoots = [];
        const fingerY = [0.3, 0.205, 0.105, 0.005];
        for (let index = 0; index < 4; index++) {
            const root = new THREE.Group();
            root.position.set(0.08, fingerY[index], 0.03);
            hand.add(root);
            const fingerCurve = new THREE.CatmullRomCurve3([
                new THREE.Vector3(0.02, 0, 0.015),
                new THREE.Vector3(-0.055, 0, 0.075),
                new THREE.Vector3(-0.135 - index * 0.006, 0, 0.115),
                new THREE.Vector3(-0.205 - index * 0.004, 0, 0.045),
                new THREE.Vector3(-0.19, 0, -0.075)
            ]);
            root.add(this.mesh(
                new THREE.TubeGeometry(
                    fingerCurve,
                    18,
                    0.032 - index * 0.0015,
                    9,
                    false),
                skinLight,
                [0, 0, 0]));
            const knuckle = this.mesh(
                new THREE.SphereGeometry(0.041 - index * 0.0015, 12, 9),
                skin,
                [0.005, 0, 0.018]);
            knuckle.scale.z = 0.78;
            root.add(knuckle);
            const fingernail = this.mesh(
                new THREE.SphereGeometry(0.021, 10, 7),
                nail,
                [-0.193, 0, -0.08]);
            fingernail.scale.set(0.72, 0.42, 0.28);
            root.add(fingernail);
            fingerRoots.push(root);
        }

        const thumbRoot = new THREE.Group();
        thumbRoot.position.set(0.16, 0.39, 0.04);
        hand.add(thumbRoot);
        const thumbCurve = new THREE.CatmullRomCurve3([
            new THREE.Vector3(0.02, 0, 0),
            new THREE.Vector3(-0.015, -0.06, 0.075),
            new THREE.Vector3(-0.1, -0.105, 0.13),
            new THREE.Vector3(-0.19, -0.13, 0.075),
            new THREE.Vector3(-0.22, -0.13, -0.02)
        ]);
        thumbRoot.add(this.mesh(
            new THREE.TubeGeometry(thumbCurve, 18, 0.039, 10, false),
            skinLight,
            [0, 0, 0]));

        hand.userData.setGrip = amount => {
            const grip = THREE.MathUtils.clamp(amount, 0, 1);
            fingerRoots.forEach((root, index) => {
                root.rotation.y = -(1 - grip) * (0.66 + index * 0.035);
                root.rotation.z = (index - 1.5) * 0.018;
            });
            thumbRoot.rotation.x = -(1 - grip) * 0.52;
        };
        hand.userData.restPosition = new THREE.Vector3(3.35, 0.62, 4.28);
        hand.userData.restRotation = new THREE.Euler(0.08, -0.2, -0.52);
        hand.userData.setGrip(0.12);
        hand.position.copy(hand.userData.restPosition);
        hand.rotation.copy(hand.userData.restRotation);
        hand.visible = false;
        this.scene.add(hand);
        return hand;
    }

    buildPaymentCard() {
        const card = new THREE.Group();
        const cardMaterial = new THREE.MeshStandardMaterial({
            color: 0xb31f2b,
            roughness: 0.35,
            metalness: 0.05
        });
        this.addBox(card, [0.78, 0.04, 0.46], [0, 0, 0], cardMaterial);
        this.addBox(card, [0.18, 0.045, 0.12], [-0.2, 0.03, -0.08], this.materials.brass);
        this.addBox(card, [0.5, 0.048, 0.045], [0.05, 0.03, 0.13], this.materials.cream);
        card.position.set(2.9, 1.15, 4.55);
        card.rotation.y = -0.38;
        card.visible = false;
        this.scene.add(card);

        const tray = new THREE.Group();
        const trayBase = this.addCylinder(
            tray,
            0.55,
            0.5,
            0.055,
            [-0.78, 1.51, 2],
            this.materials.blackMetal,
            32);
        trayBase.scale.z = 0.7;
        this.scene.add(tray);
        card.userData.trayPosition = new THREE.Vector3(-0.78, 1.62, 2.02);
        return card;
    }

    buildReceipt() {
        const receipt = new THREE.Group();
        const paper = this.addBox(
            receipt,
            [0.42, 0.025, 0.64],
            [0, 0, 0],
            this.materials.cream,
            false);
        const linesMaterial = new THREE.MeshBasicMaterial({ color: 0x4b3526 });
        for (let index = 0; index < 4; index++) {
            this.addBox(
                receipt,
                [0.25 - index * 0.025, 0.027, 0.015],
                [0, 0.015, -0.2 + index * 0.12],
                linesMaterial,
                false);
        }
        paper.rotation.y = 0.05;
        receipt.position.set(0.75, 1.58, 1.7);
        receipt.visible = false;
        this.scene.add(receipt);
        return receipt;
    }

    buildPolishSet() {
        const group = new THREE.Group();
        const glass = this.mesh(
            new THREE.CylinderGeometry(0.18, 0.15, 0.48, 24, 1, true),
            this.materials.glass,
            [0, 0, 0]);
        group.add(glass);
        const towel = this.mesh(
            new THREE.BoxGeometry(0.42, 0.015, 0.34),
            this.materials.cream,
            [0, -0.02, 0.02]);
        towel.rotation.y = 0.22;
        group.add(towel);
        group.position.set(0.85, 2.12, 0.5);
        group.visible = false;
        this.scene.add(group);
        return group;
    }

    buildWipeCloth() {
        const cloth = this.mesh(
            new THREE.BoxGeometry(0.52, 0.035, 0.34),
            new THREE.MeshStandardMaterial({ color: 0xe4d7bc, roughness: 0.92 }),
            [1.1, 1.5, 1.95]);
        cloth.visible = false;
        this.scene.add(cloth);
        return cloth;
    }

    buildLighting() {
        this.hemiLight = new THREE.HemisphereLight(0x846f57, 0x24130a, 1.25);
        this.scene.add(this.hemiLight);
        this.keyLight = new THREE.SpotLight(0xffc56f, 110, 15, Math.PI / 5, 0.55, 1.3);
        this.keyLight.position.set(-2.7, 5.05, 0.1);
        this.keyLight.target.position.set(0, 1.6, 0.2);
        this.keyLight.castShadow = true;
        this.keyLight.shadow.mapSize.set(1024, 1024);
        this.keyLight.shadow.bias = -0.0002;
        this.scene.add(this.keyLight, this.keyLight.target);
        this.fillLight = new THREE.PointLight(0xff9d56, 38, 9, 1.7);
        this.fillLight.position.set(2.7, 4.85, 0.15);
        this.scene.add(this.fillLight);
        const windowLight = new THREE.PointLight(0x6d94bf, 16, 7, 2);
        windowLight.position.set(-4.2, 3.3, -2.5);
        this.scene.add(windowLight);
        const counterGlow = new THREE.PointLight(0xd17832, 8, 4, 2);
        counterGlow.position.set(0, 1.3, 2.4);
        this.scene.add(counterGlow);

        const dustGeometry = new THREE.BufferGeometry();
        const dustPositions = new Float32Array(180 * 3);
        for (let index = 0; index < 180; index++) {
            dustPositions[index * 3] = (Math.random() - 0.5) * 11;
            dustPositions[index * 3 + 1] = 0.5 + Math.random() * 4.5;
            dustPositions[index * 3 + 2] = -3.8 + Math.random() * 9;
        }
        dustGeometry.setAttribute("position", new THREE.BufferAttribute(dustPositions, 3));
        this.dust = new THREE.Points(
            dustGeometry,
            new THREE.PointsMaterial({
                color: 0xe6bd7a,
                size: 0.018,
                transparent: true,
                opacity: 0.24,
                depthWrite: false
            }));
        this.scene.add(this.dust);
    }

    bindEvents() {
        this.glassAtPointer = event => {
            if (this.busy || !this.active) {
                return null;
            }
            const bounds = this.renderer.domElement.getBoundingClientRect();
            if (!bounds.width || !bounds.height) {
                return null;
            }
            this.pointerRay.set(
                ((event.clientX - bounds.left) / bounds.width) * 2 - 1,
                -((event.clientY - bounds.top) / bounds.height) * 2 + 1);
            this.raycaster.setFromCamera(this.pointerRay, this.camera);
            const glasses = Object.values(this.drinkGlasses)
                .filter(glass =>
                    glass.visible
                    && glass.userData.drinkId
                    && glass.userData.fill.scale.y > 0.01);
            const hit = this.raycaster.intersectObjects(glasses, true)[0]?.object;
            let candidate = hit;
            while (candidate && !glasses.includes(candidate)) {
                candidate = candidate.parent;
            }
            return candidate || null;
        };
        this.handlePointerMove = event => {
            const bounds = this.host.getBoundingClientRect();
            if (!bounds.width || !bounds.height) {
                return;
            }
            this.pointerLook.x = ((event.clientX - bounds.left) / bounds.width - 0.5) * 2;
            this.pointerLook.y = ((event.clientY - bounds.top) / bounds.height - 0.5) * 2;
            this.renderer.domElement.style.cursor =
                this.glassAtPointer(event) ? "pointer" : "";
        };
        this.handlePointerLeave = () => {
            this.pointerLook.set(0, 0);
            this.renderer.domElement.style.cursor = "";
        };
        this.handleGlassClick = event => {
            const glass = this.glassAtPointer(event);
            if (!glass) {
                return;
            }
            this.sceneElement.dispatchEvent(new CustomEvent(
                "bartenderdrinkselect",
                {
                    bubbles: true,
                    detail: { drinkId: glass.userData.drinkId }
                }));
        };
        this.host.addEventListener("pointermove", this.handlePointerMove);
        this.host.addEventListener("pointerleave", this.handlePointerLeave);
        this.renderer.domElement.addEventListener("click", this.handleGlassClick);
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
        const mouth = this.bartender.userData.mouth;
        const values = {
            closed: [1, 0.18, 0.24],
            narrow: [0.86, 0.48, 0.3],
            round: [0.68, 0.92, 0.42],
            open: [1, 0.82, 0.34]
        }[pose] || [1, 0.18, 0.24];
        mouth.scale.set(values[0], values[1], values[2]);
    }

    configureDrink(drinkId) {
        const config = drinkCatalog[drinkId] || defaultDrink;
        this.drinkGlass = this.drinkGlasses[config.category] || this.drinkGlasses.beer;
        this.activeDrink = { id: drinkId || "lightBeer", config };
        this.drinkGlass.userData.drinkId = drinkId || "lightBeer";
        this.drinkGlass.userData.liquid.material.color.setHex(config.color);
        this.drinkGlass.userData.foam.material.color.setHex(config.foam || config.color);
        this.drinkGlass.scale.setScalar(config.scale);
        this.drinkGlass.userData.foam.visible = Boolean(config.foam);
        this.drinkGlass.userData.bubbles.forEach(bubble => {
            bubble.visible = config.bubbles;
        });
        return config;
    }

    setNormalizedFill(normalizedFill) {
        const normalized = THREE.MathUtils.clamp(Number(normalizedFill ?? 0), 0, 1);
        const { fill, foam, bubbles, fillBase, fillHeight } = this.drinkGlass.userData;
        fill.scale.y = Math.max(0.001, normalized);
        foam.position.y = fillBase + normalized * fillHeight;
        if (normalized <= 0.01) {
            foam.visible = false;
            bubbles.forEach(bubble => {
                bubble.visible = false;
            });
        }
    }

    setFillLevel(fillLevel) {
        const normalized = THREE.MathUtils.clamp(Number(fillLevel ?? 0) / 3, 0, 1);
        this.setNormalizedFill(normalized);
    }

    servedPosition(category) {
        return new THREE.Vector3({
            beer: 0.05,
            spirit: 0.82,
            wine: 1.55,
            nonAlcoholic: 2.26
        }[category] ?? 0.05, 1.39, 3.15);
    }

    applySnapshot(activeDrinks) {
        if (this.busy) {
            return;
        }
        Object.values(this.drinkGlasses).forEach(glass => {
            if (glass.parent !== this.scene) {
                this.scene.attach(glass);
            }
            glass.visible = false;
        });
        for (const activeDrink of activeDrinks || []) {
            const config = this.configureDrink(activeDrink.id);
            this.scene.attach(this.drinkGlass);
            this.drinkGlass.position.copy(this.servedPosition(config.category));
            this.drinkGlass.rotation.set(0, 0, 0);
            this.drinkGlass.visible = true;
            this.setFillLevel(activeDrink.fillLevel);
        }
        if (!activeDrinks?.length) {
            this.activeDrink = null;
        }
    }

    easeInOut(value) {
        return value < 0.5
            ? 4 * value * value * value
            : 1 - Math.pow(-2 * value + 2, 3) / 2;
    }

    easeOut(value) {
        return 1 - Math.pow(1 - value, 3);
    }

    tween(duration, update, token, easing = value => this.easeInOut(value)) {
        const actualDuration = reducedMotion ? Math.min(duration, 80) : duration;
        return new Promise(resolve => {
            const start = performance.now();
            const frame = now => {
                if (token !== this.generation) {
                    resolve(false);
                    return;
                }
                const raw = Math.min(1, (now - start) / actualDuration);
                update(easing(raw), raw);
                if (raw < 1) {
                    requestAnimationFrame(frame);
                } else {
                    resolve(true);
                }
            };
            requestAnimationFrame(frame);
        });
    }

    delay(duration, token) {
        if (reducedMotion) {
            return Promise.resolve(token === this.generation);
        }
        return new Promise(resolve => {
            window.setTimeout(() => resolve(token === this.generation), duration);
        });
    }

    async pourFromTap(config, token) {
        const tap = this.taps[config.source];
        const { leftArm, leftElbow, leftHand } = this.bartender.userData;
        this.drinkGlass.position.set(tap.x, 1.37, 1.46);
        this.drinkGlass.visible = true;

        if (!await this.tween(720, value => {
            this.bartender.position.x = -0.5 * value;
            this.bartender.rotation.z = 0.08 * value;
            leftArm.rotation.z = -0.14 - 1.05 * value;
            leftArm.rotation.x = -0.35 * value;
            leftElbow.rotation.z = -0.75 * value;
            leftHand.userData.setGrip(0.18 + 0.82 * value);
            tap.handle.rotation.z = -0.42 * value;
        }, token)) {
            return false;
        }

        tap.stream.material.color.setHex(config.color);
        tap.stream.visible = true;
        if (!await this.tween(2600, (value, raw) => {
            this.setNormalizedFill(value);
            tap.stream.scale.x =
                tap.stream.scale.z =
                    0.84 + Math.sin(raw * Math.PI * 12) * 0.12;
        }, token, value => this.easeOut(value))) {
            return false;
        }
        tap.stream.visible = false;

        if (!await this.tween(650, value => {
            this.bartender.position.x = -0.5 + 0.5 * value;
            this.bartender.rotation.z = 0.08 * (1 - value);
            leftArm.rotation.z = -1.19 + 1.05 * value;
            leftArm.rotation.x = -0.35 * (1 - value);
            leftElbow.rotation.z = -0.75 * (1 - value);
            leftHand.userData.setGrip(1 - 0.82 * value);
            tap.handle.rotation.z = -0.42 * (1 - value);
        }, token)) {
            return false;
        }
        return true;
    }

    async pourFromBottle(config, token) {
        const { rightArm, rightElbow, rightHand } = this.bartender.userData;
        const stream = this.bottle.userData.stream;
        const bottleStart = new THREE.Vector3(2.05, 1.37, 1.45);
        const bottlePour = new THREE.Vector3(1.78, 2.25, 1.58);
        this.drinkGlass.position.set(1.42, 1.37, 1.55);
        this.drinkGlass.visible = true;
        this.bottle.visible = true;

        if (!await this.tween(720, value => {
            rightArm.rotation.x = -0.88 * value;
            rightArm.rotation.z = 0.14 + 0.58 * value;
            rightElbow.rotation.x = -0.62 * value;
            rightElbow.rotation.z = 0.42 * value;
            rightHand.userData.setGrip(0.18 + 0.82 * value);
            this.bottle.position.lerpVectors(bottleStart, bottlePour, value);
            this.bottle.rotation.z = 2.02 * value;
        }, token, value => this.easeOut(value))) {
            return false;
        }

        stream.material.color.setHex(config.color);
        stream.visible = true;
        if (!await this.tween(2600, (value, raw) => {
            this.setNormalizedFill(value);
            stream.scale.x =
                stream.scale.z =
                    0.84 + Math.sin(raw * Math.PI * 12) * 0.1;
            this.bottle.rotation.z = 2.02 + Math.sin(raw * Math.PI * 2) * 0.035;
        }, token, value => this.easeOut(value))) {
            return false;
        }
        stream.visible = false;

        if (!await this.tween(650, value => {
            rightArm.rotation.x = -0.88 * (1 - value);
            rightArm.rotation.z = 0.72 - 0.58 * value;
            rightElbow.rotation.x = -0.62 * (1 - value);
            rightElbow.rotation.z = 0.42 * (1 - value);
            rightHand.userData.setGrip(1 - 0.82 * value);
            this.bottle.position.lerpVectors(bottlePour, bottleStart, value);
            this.bottle.rotation.z = 2.02 * (1 - value);
        }, token)) {
            return false;
        }
        return true;
    }

    async serveDrink(token) {
        const { rightArm, rightElbow, rightHand } = this.bartender.userData;
        const start = this.drinkGlass.position.clone();
        const end = this.servedPosition(this.activeDrink?.config.category);

        if (!await this.tween(520, value => {
            rightArm.rotation.x = -1.12 * value;
            rightArm.rotation.z = 0.14 + 0.38 * value;
            rightElbow.rotation.x = -0.7 * value;
            rightHand.userData.setGrip(0.18 + 0.62 * value);
            this.bartender.position.z = -0.72 + 0.12 * value;
        }, token)) {
            return false;
        }
        if (!await this.tween(1050, value => {
            this.drinkGlass.position.lerpVectors(start, end, value);
            this.drinkGlass.rotation.z = -0.035 * Math.sin(value * Math.PI);
            rightArm.rotation.z = 0.52 - 0.52 * value;
        }, token, value => this.easeOut(value))) {
            return false;
        }
        if (!await this.tween(470, value => {
            rightArm.rotation.x = -1.12 * (1 - value);
            rightArm.rotation.z = 0.14 * value;
            rightElbow.rotation.x = -0.7 * (1 - value);
            rightHand.userData.setGrip(0.8 - 0.62 * value);
            this.bartender.position.z = -0.6 - 0.12 * value;
        }, token)) {
            return false;
        }
        this.resetBartenderPose();
        return true;
    }

    async pourAndServe(command, token) {
        const config = this.configureDrink(command.drinkId);
        this.setFillLevel(0);
        this.drinkGlass.visible = true;
        const poured = config.source === "light" || config.source === "dark"
            ? await this.pourFromTap(config, token)
            : await this.pourFromBottle(config, token);
        if (!poured) {
            return false;
        }
        return this.serveDrink(token);
    }

    async drink(command, token) {
        this.configureDrink(command.drinkId);
        if (!this.drinkGlass.visible) {
            return true;
        }
        const hand = this.guestHand;
        const handStart = hand.userData.restPosition.clone();
        const handStartRotation = hand.userData.restRotation.clone();
        const glassStart = this.drinkGlass.position.clone();
        const glassStartRotation = this.drinkGlass.rotation.clone();
        const handGrip = new THREE.Vector3(glassStart.x + 0.37, 1.48, 3.34);
        const handGripRotation = new THREE.Euler(0.04, -0.44, -0.08);
        const handLift = new THREE.Vector3(0.56, 1.8, 4.92);
        const handLiftRotation = new THREE.Euler(0.03, -0.38, -0.78);
        const startFill = this.drinkGlass.userData.fill.scale.y;
        const targetFill = THREE.MathUtils.clamp(Number(command.fillLevel ?? 0) / 3, 0, 1);
        hand.visible = true;
        hand.userData.setGrip(0.12);

        if (!await this.tween(650, value => {
            hand.position.lerpVectors(handStart, handGrip, value);
            hand.rotation.x = THREE.MathUtils.lerp(
                handStartRotation.x,
                handGripRotation.x,
                value);
            hand.rotation.y = THREE.MathUtils.lerp(
                handStartRotation.y,
                handGripRotation.y,
                value);
            hand.rotation.z = THREE.MathUtils.lerp(
                handStartRotation.z,
                handGripRotation.z,
                value);
            hand.userData.setGrip(0.12 + value * 0.88);
        }, token, value => this.easeOut(value))) {
            return false;
        }
        hand.attach(this.drinkGlass);

        if (!await this.tween(1000, value => {
            hand.position.lerpVectors(handGrip, handLift, value);
            hand.rotation.x = THREE.MathUtils.lerp(
                handGripRotation.x,
                handLiftRotation.x,
                value);
            hand.rotation.y = THREE.MathUtils.lerp(
                handGripRotation.y,
                handLiftRotation.y,
                value);
            hand.rotation.z = THREE.MathUtils.lerp(
                handGripRotation.z,
                handLiftRotation.z,
                value);
        }, token)) {
            return false;
        }
        if (!await this.delay(260, token)) {
            return false;
        }
        if (!await this.tween(1650, value => {
            this.setNormalizedFill(THREE.MathUtils.lerp(startFill, targetFill, value));
            hand.rotation.z =
                handLiftRotation.z - 0.17 * Math.sin(value * Math.PI);
            hand.userData.setGrip(1 - Math.sin(value * Math.PI) * 0.06);
        }, token, value => this.easeOut(value))) {
            return false;
        }
        if (targetFill <= 0.01) {
            this.drinkGlass.userData.foam.visible = false;
            this.drinkGlass.userData.bubbles.forEach(bubble => {
                bubble.visible = false;
            });
        }

        if (!await this.tween(900, value => {
            hand.position.lerpVectors(handLift, handGrip, value);
            hand.rotation.x = THREE.MathUtils.lerp(
                handLiftRotation.x,
                handGripRotation.x,
                value);
            hand.rotation.y = THREE.MathUtils.lerp(
                handLiftRotation.y,
                handGripRotation.y,
                value);
            hand.rotation.z = THREE.MathUtils.lerp(
                handLiftRotation.z,
                handGripRotation.z,
                value);
        }, token)) {
            return false;
        }
        this.scene.attach(this.drinkGlass);
        this.drinkGlass.position.copy(glassStart);
        this.drinkGlass.rotation.copy(glassStartRotation);

        if (!await this.tween(480, value => {
            hand.position.lerpVectors(handGrip, handStart, value);
            hand.rotation.x = THREE.MathUtils.lerp(
                handGripRotation.x,
                handStartRotation.x,
                value);
            hand.rotation.y = THREE.MathUtils.lerp(
                handGripRotation.y,
                handStartRotation.y,
                value);
            hand.rotation.z = THREE.MathUtils.lerp(
                handGripRotation.z,
                handStartRotation.z,
                value);
            hand.userData.setGrip(1 - value * 0.88);
        }, token, value => this.easeOut(value))) {
            return false;
        }
        hand.visible = false;
        return true;
    }

    async offerSnack(token) {
        const bowl = this.room.userData.snackBowl;
        const { leftArm, leftElbow, leftHand } = this.bartender.userData;
        const start = bowl.position.clone();
        const end = new THREE.Vector3(-0.2, 1.5, 3.05);
        bowl.visible = true;
        bowl.userData.sticks.forEach(stick => {
            stick.visible = true;
        });

        if (!await this.tween(520, value => {
            leftArm.rotation.x = -0.82 * value;
            leftArm.rotation.z = -0.14 - 0.42 * value;
            leftElbow.rotation.x = -0.55 * value;
            leftHand.userData.setGrip(0.18 + 0.55 * value);
        }, token)) {
            return false;
        }
        if (!await this.tween(900, value => {
            bowl.position.lerpVectors(start, end, value);
            bowl.rotation.y = Math.sin(value * Math.PI) * 0.12;
            leftHand.userData.setGrip(0.73 - Math.sin(value * Math.PI) * 0.08);
        }, token, value => this.easeOut(value))) {
            return false;
        }
        if (!await this.tween(420, value => {
            leftArm.rotation.x = -0.82 * (1 - value);
            leftArm.rotation.z = -0.56 + 0.42 * value;
            leftElbow.rotation.x = -0.55 * (1 - value);
            leftHand.userData.setGrip(0.73 - 0.55 * value);
        }, token)) {
            return false;
        }
        this.resetBartenderPose();
        return true;
    }

    async takeSnack(token) {
        const bowl = this.room.userData.snackBowl;
        const hand = this.guestHand;
        const start = hand.userData.restPosition.clone();
        const startRotation = hand.userData.restRotation.clone();
        const reach = new THREE.Vector3(0.56, 1.56, 3.28);
        const reachRotation = new THREE.Euler(-0.18, -0.54, -0.28);
        const mouth = new THREE.Vector3(0.38, 2.08, 5.15);
        const stick = bowl.userData.sticks.find(item => item.visible);
        hand.visible = true;
        hand.userData.setGrip(0.12);

        if (!await this.tween(620, value => {
            hand.position.lerpVectors(start, reach, value);
            hand.rotation.x = THREE.MathUtils.lerp(startRotation.x, reachRotation.x, value);
            hand.rotation.y = THREE.MathUtils.lerp(startRotation.y, reachRotation.y, value);
            hand.rotation.z = THREE.MathUtils.lerp(startRotation.z, reachRotation.z, value);
            hand.userData.setGrip(0.12 + value * 0.72);
        }, token, value => this.easeOut(value))) {
            return false;
        }
        if (stick) {
            stick.visible = false;
        }
        if (!await this.tween(720, value => {
            hand.position.lerpVectors(reach, mouth, value);
            hand.rotation.z = THREE.MathUtils.lerp(reachRotation.z, -0.72, value);
            hand.userData.setGrip(0.84 + Math.sin(value * Math.PI) * 0.1);
        }, token)) {
            return false;
        }
        if (!await this.delay(120, token)) {
            return false;
        }
        if (!await this.tween(520, value => {
            hand.position.lerpVectors(mouth, start, value);
            hand.rotation.x = THREE.MathUtils.lerp(reachRotation.x, startRotation.x, value);
            hand.rotation.y = THREE.MathUtils.lerp(reachRotation.y, startRotation.y, value);
            hand.rotation.z = THREE.MathUtils.lerp(-0.72, startRotation.z, value);
            hand.userData.setGrip(0.84 - value * 0.72);
        }, token, value => this.easeOut(value))) {
            return false;
        }
        hand.visible = false;
        return true;
    }

    async showBill(token) {
        const start = new THREE.Vector3(0.75, 1.58, 1.7);
        const end = new THREE.Vector3(-0.78, 1.57, 2.05);
        const { rightArm, rightElbow, rightHand } = this.bartender.userData;
        this.receipt.position.copy(start);
        this.receipt.visible = true;
        if (!await this.tween(680, value => {
            rightArm.rotation.x = -0.95 * value;
            rightArm.rotation.z = 0.14 + 0.32 * value;
            rightElbow.rotation.x = -0.58 * value;
            rightHand.userData.setGrip(0.18 + 0.62 * value);
            this.receipt.position.lerpVectors(start, end, value);
        }, token, value => this.easeOut(value))) {
            return false;
        }
        if (!await this.tween(420, value => {
            rightArm.rotation.x = -0.95 * (1 - value);
            rightArm.rotation.z = 0.46 - 0.32 * value;
            rightElbow.rotation.x = -0.58 * (1 - value);
            rightHand.userData.setGrip(0.8 - 0.62 * value);
        }, token)) {
            return false;
        }
        this.resetBartenderPose();
        return true;
    }

    async acceptPayment(token) {
        const start = new THREE.Vector3(2.9, 1.15, 4.55);
        const end = this.paymentCard.userData.trayPosition;
        this.paymentCard.position.copy(start);
        this.paymentCard.rotation.set(0, -0.38, 0);
        this.paymentCard.visible = true;
        if (!await this.tween(1050, value => {
            this.paymentCard.position.lerpVectors(start, end, value);
            this.paymentCard.rotation.y = -0.38 + 0.38 * value;
            this.paymentCard.rotation.z = 0.08 * Math.sin(value * Math.PI);
        }, token, value => this.easeOut(value))) {
            return false;
        }
        if (!await this.delay(420, token)) {
            return false;
        }
        if (!await this.tween(560, value => {
            this.bartender.userData.head.rotation.x =
                0.14 * Math.sin(value * Math.PI);
        }, token)) {
            return false;
        }
        this.bartender.userData.head.rotation.x = 0;
        return true;
    }

    async returnChange(token) {
        const start = this.paymentCard.userData.trayPosition.clone();
        const end = new THREE.Vector3(1.65, 1.54, 3.62);
        const coin = this.mesh(
            new THREE.CylinderGeometry(0.16, 0.16, 0.035, 24),
            this.materials.brass,
            [start.x, start.y, start.z]);
        coin.rotation.x = Math.PI / 2;
        this.scene.add(coin);
        if (!await this.tween(760, value => {
            coin.position.lerpVectors(start, end, value);
            coin.rotation.y = value * Math.PI * 3;
        }, token, value => this.easeOut(value))) {
            this.scene.remove(coin);
            return false;
        }
        await this.delay(220, token);
        this.scene.remove(coin);
        return token === this.generation;
    }

    async clearGlass(command, token) {
        this.configureDrink(command.drinkId);
        if (!this.drinkGlass.visible) {
            return true;
        }
        const { rightArm, rightElbow, rightHand } = this.bartender.userData;
        const start = this.drinkGlass.position.clone();
        const end = new THREE.Vector3(2.15, 1.39, 1.55);
        if (!await this.tween(540, value => {
            rightArm.rotation.x = -1.08 * value;
            rightArm.rotation.z = 0.14 + 0.46 * value;
            rightElbow.rotation.x = -0.62 * value;
            rightHand.userData.setGrip(0.18 + 0.76 * value);
        }, token)) {
            return false;
        }
        if (!await this.tween(760, value => {
            this.drinkGlass.position.lerpVectors(start, end, value);
            this.drinkGlass.rotation.z = 0.06 * Math.sin(value * Math.PI);
            rightArm.rotation.z = 0.6 - 0.26 * value;
        }, token, value => this.easeOut(value))) {
            return false;
        }
        this.drinkGlass.visible = false;
        if (!await this.tween(420, value => {
            rightArm.rotation.x = -1.08 * (1 - value);
            rightArm.rotation.z = 0.34 - 0.2 * value;
            rightElbow.rotation.x = -0.62 * (1 - value);
            rightHand.userData.setGrip(0.94 - 0.76 * value);
        }, token)) {
            return false;
        }
        this.activeDrink = null;
        this.resetBartenderPose();
        return true;
    }

    async polishGlass(token) {
        const {
            leftArm,
            rightArm,
            leftElbow,
            rightElbow,
            leftHand,
            rightHand
        } = this.bartender.userData;
        this.polishSet.visible = true;
        if (!await this.tween(520, value => {
            leftArm.rotation.x = -0.78 * value;
            leftArm.rotation.z = -0.14 - 0.38 * value;
            rightArm.rotation.x = -0.84 * value;
            rightArm.rotation.z = 0.14 + 0.36 * value;
            leftElbow.rotation.x = -0.46 * value;
            rightElbow.rotation.x = -0.54 * value;
            leftHand.userData.setGrip(0.18 + 0.66 * value);
            rightHand.userData.setGrip(0.18 + 0.7 * value);
        }, token)) {
            return false;
        }
        if (!await this.tween(1450, (value, raw) => {
            this.polishSet.rotation.y = raw * Math.PI * 5;
            this.polishSet.rotation.z = Math.sin(raw * Math.PI * 6) * 0.08;
            leftHand.userData.setGrip(0.84 + Math.sin(raw * Math.PI * 6) * 0.08);
            rightHand.userData.setGrip(0.88 - Math.sin(raw * Math.PI * 6) * 0.06);
        }, token, value => value)) {
            return false;
        }
        if (!await this.tween(520, value => {
            leftArm.rotation.x = -0.78 * (1 - value);
            leftArm.rotation.z = -0.52 + 0.38 * value;
            rightArm.rotation.x = -0.84 * (1 - value);
            rightArm.rotation.z = 0.5 - 0.36 * value;
            leftElbow.rotation.x = -0.46 * (1 - value);
            rightElbow.rotation.x = -0.54 * (1 - value);
            leftHand.userData.setGrip(0.84 - 0.66 * value);
            rightHand.userData.setGrip(0.88 - 0.7 * value);
        }, token)) {
            return false;
        }
        this.polishSet.visible = false;
        this.polishSet.rotation.set(0, 0, 0);
        this.resetBartenderPose();
        return true;
    }

    async wipeCounter(token) {
        const { rightArm, rightElbow, rightHand } = this.bartender.userData;
        const start = new THREE.Vector3(1.1, 1.5, 1.95);
        const left = new THREE.Vector3(-1.3, 1.5, 1.95);
        const right = new THREE.Vector3(1.65, 1.5, 1.95);
        this.wipeCloth.position.copy(start);
        this.wipeCloth.visible = true;
        if (!await this.tween(480, value => {
            rightArm.rotation.x = -1.02 * value;
            rightArm.rotation.z = 0.14 + 0.38 * value;
            rightElbow.rotation.x = -0.66 * value;
            rightHand.userData.setGrip(0.18 + 0.72 * value);
        }, token)) {
            return false;
        }
        if (!await this.tween(1150, (value, raw) => {
            const segment = raw < 0.5 ? raw * 2 : (raw - 0.5) * 2;
            this.wipeCloth.position.lerpVectors(raw < 0.5 ? left : right, raw < 0.5 ? right : left, segment);
            this.wipeCloth.rotation.y = Math.sin(raw * Math.PI * 4) * 0.16;
            rightArm.rotation.z = 0.52 + Math.sin(raw * Math.PI * 4) * 0.2;
            rightHand.userData.setGrip(0.9 - Math.sin(raw * Math.PI * 4) * 0.06);
        }, token, value => value)) {
            return false;
        }
        if (!await this.tween(480, value => {
            rightArm.rotation.x = -1.02 * (1 - value);
            rightArm.rotation.z = 0.52 - 0.38 * value;
            rightElbow.rotation.x = -0.66 * (1 - value);
            rightHand.userData.setGrip(0.9 - 0.72 * value);
        }, token)) {
            return false;
        }
        this.wipeCloth.visible = false;
        this.resetBartenderPose();
        return true;
    }

    async lastCall(token) {
        const normalKey = this.keyLight.intensity;
        const normalFill = this.fillLight.intensity;
        const { rightArm, rightElbow, rightHand } = this.bartender.userData;
        if (!await this.tween(650, value => {
            this.keyLight.intensity = THREE.MathUtils.lerp(normalKey, 42, value);
            this.fillLight.intensity = THREE.MathUtils.lerp(normalFill, 12, value);
            rightArm.rotation.z = 0.14 + 1.1 * value;
            rightElbow.rotation.z = -0.45 * value;
            rightHand.userData.setGrip(0.18 + 0.24 * value);
            this.bartender.userData.head.rotation.x =
                0.12 * Math.sin(value * Math.PI);
        }, token)) {
            return false;
        }
        if (!await this.delay(420, token)) {
            return false;
        }
        if (!await this.tween(650, value => {
            this.keyLight.intensity = THREE.MathUtils.lerp(42, normalKey, value);
            this.fillLight.intensity = THREE.MathUtils.lerp(12, normalFill, value);
            rightArm.rotation.z = 1.24 - 1.1 * value;
            rightElbow.rotation.z = -0.45 * (1 - value);
            rightHand.userData.setGrip(0.42 - 0.24 * value);
        }, token)) {
            return false;
        }
        this.resetBartenderPose();
        return true;
    }

    async headGesture(accepted, token) {
        return this.tween(620, (value, raw) => {
            if (accepted) {
                this.bartender.userData.head.rotation.x =
                    Math.sin(raw * Math.PI * 2) * 0.11;
            } else {
                this.bartender.userData.head.rotation.y =
                    Math.sin(raw * Math.PI * 3) * 0.16;
            }
        }, token, value => value).then(result => {
            this.bartender.userData.head.rotation.set(0, 0, 0);
            return result;
        });
    }

    async playCommand(command) {
        if (!command || !this.active) {
            return true;
        }
        const token = this.generation;
        this.busy = true;
        let completed = true;
        try {
            switch (command.type) {
                case "pourAndServe":
                    completed = await this.pourAndServe(command, token);
                    break;
                case "drink":
                    completed = await this.drink(command, token);
                    break;
                case "offerSnack":
                    completed = await this.offerSnack(token);
                    break;
                case "takeSnack":
                    completed = await this.takeSnack(token);
                    break;
                case "showBill":
                    completed = await this.showBill(token);
                    break;
                case "paymentAccepted":
                    completed = await this.acceptPayment(token);
                    break;
                case "returnChange":
                    completed = await this.returnChange(token);
                    break;
                case "clearGlass":
                    completed = await this.clearGlass(command, token);
                    break;
                case "polishGlass":
                    completed = await this.polishGlass(token);
                    break;
                case "wipeCounter":
                    completed = await this.wipeCounter(token);
                    break;
                case "lastCall":
                    completed = await this.lastCall(token);
                    break;
                case "markUnavailable":
                case "paymentRejected":
                    completed = await this.headGesture(false, token);
                    break;
                default:
                    break;
            }
        } finally {
            if (token === this.generation) {
                this.busy = false;
            }
        }
        return Boolean(completed && token === this.generation);
    }

    resetBartenderPose() {
        const {
            leftArm,
            rightArm,
            leftElbow,
            rightElbow,
            leftHand,
            rightHand
        } = this.bartender.userData;
        this.bartender.position.set(0, 1.35, -0.72);
        this.bartender.rotation.set(0, 0, 0);
        leftArm.rotation.set(0, 0, -0.14);
        rightArm.rotation.set(0, 0, 0.14);
        leftElbow.rotation.set(0, 0, 0);
        rightElbow.rotation.set(0, 0, 0);
        leftHand.userData.setGrip(0.18);
        rightHand.userData.setGrip(0.18);
        this.bartender.userData.head.rotation.set(0, 0, 0);
    }

    resetObjects() {
        Object.values(this.drinkGlasses).forEach(glass => {
            this.drinkGlass = glass;
            if (glass.parent !== this.scene) {
                this.scene.attach(glass);
            }
            glass.visible = false;
            glass.position.set(-2.55, 1.37, 1.46);
            glass.rotation.set(0, 0, 0);
            glass.scale.setScalar(1);
            this.setFillLevel(0);
        });
        this.drinkGlass = this.drinkGlasses.beer;
        this.guestHand.visible = false;
        this.guestHand.position.copy(this.guestHand.userData.restPosition);
        this.guestHand.rotation.copy(this.guestHand.userData.restRotation);
        this.guestHand.userData.setGrip(0.12);
        this.paymentCard.visible = false;
        this.paymentCard.position.set(2.9, 1.15, 4.55);
        this.paymentCard.rotation.set(0, -0.38, 0);
        this.receipt.visible = false;
        this.polishSet.visible = false;
        this.wipeCloth.visible = false;
        this.bottle.visible = true;
        this.bottle.position.set(2.05, 1.37, 1.45);
        this.bottle.rotation.set(0, 0, 0);
        this.bottle.userData.stream.visible = false;
        Object.values(this.taps).forEach(tap => {
            tap.handle.rotation.z = 0;
            tap.stream.visible = false;
        });
        const bowl = this.room.userData.snackBowl;
        bowl.position.set(-3.75, 1.51, 2.18);
        bowl.rotation.set(0, 0, 0);
        bowl.userData.sticks.forEach(stick => {
            stick.visible = true;
        });
        this.keyLight.intensity = 110;
        this.fillLight.intensity = 38;
        this.activeDrink = null;
        this.resetBartenderPose();
    }

    cancel({ reset = false } = {}) {
        this.generation += 1;
        this.busy = false;
        this.resetBartenderPose();
        if (reset) {
            this.resetObjects();
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
        this.smoothLook.lerp(this.pointerLook, Math.min(1, delta * 2.8));
        this.camera.position.x = this.smoothLook.x * 0.18;
        this.camera.position.y = 2.15 - this.smoothLook.y * 0.07;
        this.camera.lookAt(
            this.cameraTarget.x + this.smoothLook.x * 0.26,
            this.cameraTarget.y - this.smoothLook.y * 0.12,
            this.cameraTarget.z);

        if (!this.busy) {
            this.bartender.position.y = 1.35 + Math.sin(elapsed * 1.45) * 0.012;
            this.bartender.userData.head.rotation.y =
                Math.sin(elapsed * 0.48) * (this.talking ? 0.025 : 0.055);
        }
        const blink = Math.sin(elapsed * 2.7) > 0.992 ? 0.08 : 1;
        this.bartender.userData.eyes.scale.y +=
            (blink - this.bartender.userData.eyes.scale.y) * 0.35;
        if (!this.busy) {
            this.keyLight.intensity =
                106 + Math.sin(elapsed * 1.9) * 3 + Math.sin(elapsed * 5.7) * 1.3;
            this.fillLight.intensity = 36 + Math.sin(elapsed * 2.3 + 1.1) * 2.5;
        }
        this.dust.rotation.y = elapsed * 0.008;
        const activeStream = [
            ...Object.values(this.taps).map(tap => tap.stream),
            this.bottle.userData.stream
        ].find(stream => stream.visible);
        if (activeStream) {
            activeStream.position.y += Math.sin(elapsed * 24) * 0.0008;
        }
        for (const glass of Object.values(this.drinkGlasses)) {
            for (const bubble of glass.userData.bubbles) {
                if (bubble.visible) {
                    bubble.position.y =
                        glass.userData.fillBase
                        + ((elapsed * 0.11 + bubble.userData.offset)
                            % glass.userData.fillHeight);
                }
            }
        }
        this.renderer.render(this.scene, this.camera);
    }
}

export function initializeBartenderThreeScenes(root = document) {
    root.querySelectorAll("[data-avatar-scene='bartender']").forEach(sceneElement => {
        if (controllers.has(sceneElement)) {
            return;
        }
        const host = sceneElement.querySelector("[data-bartender-three-host]");
        if (!host) {
            return;
        }
        try {
            controllers.set(
                sceneElement,
                new BartenderThreeController(sceneElement, host));
        } catch (error) {
            host.querySelector("[data-bartender-three-loading]")?.setAttribute("hidden", "");
            const fallback = host.querySelector("[data-bartender-three-fallback]");
            if (fallback) {
                fallback.hidden = false;
            }
            console.error("The Three.js bartender could not be initialized.", error);
        }
    });
}

export function getBartenderThreeController(sceneElement) {
    return sceneElement ? controllers.get(sceneElement) || null : null;
}
