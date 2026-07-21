import * as THREE from "https://cdn.jsdelivr.net/npm/three@0.180.0/build/three.module.js";

const reducedMotion = window.matchMedia("(prefers-reduced-motion: reduce)").matches;
const controllers = new WeakMap();

class KasiaCashierThreeController {
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
            host.querySelector("[data-kasia-three-loading]")?.setAttribute("hidden", "");
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
        this.scene.background = new THREE.Color(0xdde8d9);
        this.scene.fog = new THREE.FogExp2(0xdce8d8, 0.025);

        this.camera = new THREE.PerspectiveCamera(43, 1, 0.08, 40);
        this.camera.position.set(0, 2.55, 7.4);
        this.cameraTarget = new THREE.Vector3(0, 2.12, -0.75);

        this.materials = this.buildMaterials();
        this.buildStore();
        this.checkout = this.buildCheckout();
        this.cashier = this.buildCashier();
        this.buildCustomerView();
        this.buildLighting();
        this.resize();
    }

    buildMaterials() {
        return {
            green: new THREE.MeshStandardMaterial({ color: 0x00a64f, roughness: 0.66 }),
            greenDark: new THREE.MeshStandardMaterial({ color: 0x08733d, roughness: 0.72 }),
            greenDeep: new THREE.MeshStandardMaterial({ color: 0x07552f, roughness: 0.78 }),
            lime: new THREE.MeshStandardMaterial({ color: 0xa8d532, roughness: 0.7 }),
            yellow: new THREE.MeshStandardMaterial({ color: 0xf4cc21, roughness: 0.64 }),
            cream: new THREE.MeshStandardMaterial({ color: 0xf4f1df, roughness: 0.86 }),
            wall: new THREE.MeshStandardMaterial({ color: 0xe8eee3, roughness: 0.94 }),
            floor: new THREE.MeshStandardMaterial({ color: 0xb9c2b6, roughness: 0.82 }),
            charcoal: new THREE.MeshStandardMaterial({ color: 0x22292a, roughness: 0.56 }),
            black: new THREE.MeshStandardMaterial({ color: 0x101515, roughness: 0.5 }),
            metal: new THREE.MeshStandardMaterial({
                color: 0xb8c1bd,
                metalness: 0.65,
                roughness: 0.36
            }),
            white: new THREE.MeshStandardMaterial({ color: 0xf7f6ed, roughness: 0.72 }),
            skin: new THREE.MeshStandardMaterial({ color: 0xd59a72, roughness: 0.72 }),
            skinLight: new THREE.MeshStandardMaterial({ color: 0xe4ad84, roughness: 0.7 }),
            cheek: new THREE.MeshStandardMaterial({ color: 0xd88972, roughness: 0.75 }),
            hair: new THREE.MeshStandardMaterial({ color: 0x4b2b20, roughness: 0.86 }),
            hairLight: new THREE.MeshStandardMaterial({ color: 0x6b4030, roughness: 0.84 }),
            eyeWhite: new THREE.MeshStandardMaterial({ color: 0xf8f6ea, roughness: 0.5 }),
            iris: new THREE.MeshStandardMaterial({ color: 0x466b4c, roughness: 0.42 }),
            mouth: new THREE.MeshStandardMaterial({ color: 0x6d2532, roughness: 0.62 }),
            lip: new THREE.MeshStandardMaterial({ color: 0xb85866, roughness: 0.62 }),
            red: new THREE.MeshStandardMaterial({ color: 0xd82b25, roughness: 0.58 }),
            orange: new THREE.MeshStandardMaterial({ color: 0xf28a21, roughness: 0.6 }),
            blue: new THREE.MeshStandardMaterial({ color: 0x2981b7, roughness: 0.58 }),
            glass: new THREE.MeshPhysicalMaterial({
                color: 0xe4f3ee,
                transparent: true,
                opacity: 0.28,
                roughness: 0.08,
                transmission: 0.42,
                thickness: 0.08,
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
            new THREE.CylinderGeometry(radiusTop, radiusBottom, height, radialSegments),
            material,
            position);
        parent.add(object);
        return object;
    }

    addBone(parent, start, end, radius, material) {
        const from = new THREE.Vector3(...start);
        const to = new THREE.Vector3(...end);
        const midpoint = from.clone().add(to).multiplyScalar(0.5);
        const bone = this.mesh(
            new THREE.CylinderGeometry(radius, radius * 0.92, from.distanceTo(to), 18),
            material,
            [midpoint.x, midpoint.y, midpoint.z]);
        bone.quaternion.setFromUnitVectors(
            new THREE.Vector3(0, 1, 0),
            to.clone().sub(from).normalize());
        parent.add(bone);
        return bone;
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
            context.fillStyle = "#00a651";
            context.fillRect(0, 0, width, height);
            context.fillStyle = "#f1d51f";
            context.fillRect(0, height - 28, width, 28);
            context.fillStyle = "#ffffff";
            context.textAlign = "center";
            context.font = "900 116px Arial";
            context.fillText("ŻABKA", width / 2, 146);
            context.fillStyle = "#163a2c";
            context.font = "700 29px Arial";
            context.fillText("BLISKO  •  SZYBKO  •  WYGODNIE", width / 2, 206);
        });
    }

    makePromoTexture() {
        return this.makeCanvasTexture((context, width, height) => {
            context.fillStyle = "#f5d421";
            context.fillRect(0, 0, width, height);
            context.fillStyle = "#08743d";
            context.textAlign = "center";
            context.font = "900 58px Arial";
            context.fillText("HOT DOG + NAPÓJ", width / 2, 84);
            context.fillStyle = "#d82b25";
            context.font = "900 72px Arial";
            context.fillText("9,99 zł", width / 2, 170);
            context.fillStyle = "#17392c";
            context.font = "700 25px Arial";
            context.fillText("zapytaj przy kasie", width / 2, 216);
        }, 640, 256);
    }

    makeRegisterTexture() {
        return this.makeCanvasTexture((context, width, height) => {
            context.fillStyle = "#10251d";
            context.fillRect(0, 0, width, height);
            context.fillStyle = "#6fe89d";
            context.font = "700 32px monospace";
            context.fillText("PARAGON", 34, 54);
            context.fillStyle = "#d9f7e3";
            context.font = "700 46px monospace";
            context.fillText("RAZEM  18,47 zł", 34, 122);
            context.fillStyle = "#8db49b";
            context.font = "24px monospace";
            context.fillText("KARTA / GOTÓWKA", 34, 174);
        }, 640, 220);
    }

    makeNameTagTexture() {
        return this.makeCanvasTexture((context, width, height) => {
            context.fillStyle = "#f5d421";
            context.fillRect(0, 0, width, height);
            context.fillStyle = "#075b34";
            context.textAlign = "center";
            context.font = "900 82px Arial";
            context.fillText("KASIA", width / 2, 100);
        }, 360, 128);
    }

    makeProductLabel(text, background, foreground = "#ffffff") {
        return this.makeCanvasTexture((context, width, height) => {
            context.fillStyle = background;
            context.fillRect(0, 0, width, height);
            context.fillStyle = foreground;
            context.textAlign = "center";
            const fontSize = text.length > 12 ? 28 : text.length > 9 ? 34 : 42;
            context.font = `900 ${fontSize}px Arial`;
            context.fillText(text, width / 2, 83);
        }, 320, 120);
    }

    addTextPlane(parent, size, position, texture, rotation = null) {
        const material = new THREE.MeshBasicMaterial({ map: texture, transparent: true });
        const plane = this.mesh(
            new THREE.PlaneGeometry(size[0], size[1]),
            material,
            position,
            false,
            false);
        if (rotation) {
            plane.rotation.set(rotation[0], rotation[1], rotation[2]);
        }
        parent.add(plane);
        return plane;
    }

    buildStore() {
        const room = new THREE.Group();
        this.scene.add(room);

        const floor = this.mesh(
            new THREE.PlaneGeometry(18, 18),
            this.materials.floor,
            [0, 0, 0],
            false,
            true);
        floor.rotation.x = -Math.PI / 2;
        room.add(floor);

        this.addBox(room, [14, 6.2, 0.25], [0, 3.1, -5.05], this.materials.wall, false);
        this.addBox(room, [0.25, 6.2, 12], [-7, 3.1, 0], this.materials.wall, false);
        this.addBox(room, [0.25, 6.2, 12], [7, 3.1, 0], this.materials.wall, false);

        for (let x = -6.5; x <= 6.5; x += 0.65) {
            this.addBox(room, [0.012, 6, 0.02], [x, 3.05, -4.9], this.materials.cream, false);
        }

        const sign = this.addTextPlane(
            room,
            [4.9, 1.22],
            [0, 5.02, -4.76],
            this.makeSignTexture());
        sign.material.toneMapped = false;

        this.buildAisleShelves(room);
        this.buildDrinkFridge(room);
        this.buildHotDogStation(room);
        this.buildCeilingLights(room);
    }

    buildAisleShelves(room) {
        const shelf = new THREE.Group();
        shelf.position.set(-3.75, 0.05, -4.2);
        room.add(shelf);

        this.addBox(shelf, [3.9, 3.55, 0.24], [0, 1.78, 0], this.materials.greenDeep, false);
        for (const y of [0.62, 1.28, 1.94, 2.6, 3.25]) {
            this.addBox(shelf, [4.15, 0.11, 0.82], [0, y, 0.35], this.materials.metal);
            this.addBox(shelf, [4.18, 0.1, 0.08], [0, y + 0.07, 0.78], this.materials.green);
        }

        const productColors = [
            this.materials.red,
            this.materials.orange,
            this.materials.yellow,
            this.materials.green,
            this.materials.blue,
            this.materials.cream
        ];
        const names = ["CHIPSY", "KAWA", "BATON", "SOK", "HERBATA", "CIASTKA"];
        for (let row = 0; row < 4; row++) {
            for (let index = 0; index < 10; index++) {
                const width = 0.25 + ((index + row) % 3) * 0.035;
                const height = 0.35 + ((index * 2 + row) % 4) * 0.035;
                const product = new THREE.Group();
                this.addBox(
                    product,
                    [width, height, 0.28],
                    [0, height / 2, 0],
                    productColors[(index + row * 2) % productColors.length]);
                if ((index + row) % 3 === 0) {
                    this.addTextPlane(
                        product,
                        [width * 0.82, height * 0.32],
                        [0, height * 0.56, 0.145],
                        this.makeProductLabel(
                            names[(index + row) % names.length],
                            "#f5f0da",
                            "#174331"));
                }
                product.position.set(-1.7 + index * 0.38, 0.68 + row * 0.66, 0.53);
                product.rotation.y = ((index % 3) - 1) * 0.025;
                shelf.add(product);
            }
        }

        this.addTextPlane(
            shelf,
            [2.7, 0.44],
            [0.55, 3.55, 0.16],
            this.makeProductLabel("SZYBKIE ZAKUPY", "#00a651"));
    }

    buildDrinkFridge(room) {
        const fridge = new THREE.Group();
        fridge.position.set(4.15, 0.05, -4.35);
        room.add(fridge);

        this.addBox(fridge, [3.7, 3.9, 0.74], [0, 1.95, 0], this.materials.charcoal, false);
        this.addBox(fridge, [3.5, 3.65, 0.1], [0, 1.94, 0.42], this.materials.glass, false);
        for (const x of [-1.72, 0, 1.72]) {
            this.addBox(fridge, [0.07, 3.7, 0.12], [x, 1.95, 0.49], this.materials.metal);
        }
        for (const y of [0.7, 1.35, 2, 2.65, 3.3]) {
            this.addBox(fridge, [3.4, 0.055, 0.52], [0, y, 0.12], this.materials.metal, false);
        }

        const bottleMaterials = [
            this.materials.red,
            this.materials.orange,
            this.materials.green,
            this.materials.blue,
            this.materials.cream
        ];
        for (let row = 0; row < 4; row++) {
            for (let index = 0; index < 14; index++) {
                const bottle = new THREE.Group();
                const material = bottleMaterials[(index + row * 2) % bottleMaterials.length];
                this.addCylinder(bottle, 0.075, 0.09, 0.42, [0, 0.21, 0], material, 14);
                this.addCylinder(bottle, 0.035, 0.055, 0.12, [0, 0.48, 0], material, 12);
                bottle.position.set(-1.5 + index * 0.23, 0.72 + row * 0.65, 0.36);
                fridge.add(bottle);
            }
        }

        const fridgeGlow = new THREE.PointLight(0xc7f0df, 12, 4.8, 2);
        fridgeGlow.position.set(0, 2.2, 1.2);
        fridge.add(fridgeGlow);
    }

    buildHotDogStation(room) {
        const station = new THREE.Group();
        station.position.set(-2.65, 1.25, -1.05);
        room.add(station);

        this.addBox(station, [1.35, 0.12, 0.72], [0, 0.02, 0], this.materials.metal);
        this.addBox(station, [1.2, 0.82, 0.08], [0, 0.46, -0.31], this.materials.charcoal);
        for (const x of [-0.4, -0.13, 0.14, 0.41]) {
            const roller = this.addCylinder(
                station,
                0.055,
                0.055,
                0.9,
                [x, 0.22, 0.02],
                this.materials.metal,
                16);
            roller.rotation.x = Math.PI / 2;
        }
        for (const x of [-0.28, 0.25]) {
            const sausage = this.addCylinder(
                station,
                0.07,
                0.07,
                0.72,
                [x, 0.3, 0.09],
                this.materials.red,
                18);
            sausage.rotation.x = Math.PI / 2;
        }
        this.addTextPlane(
            station,
            [1.25, 0.5],
            [0, 0.76, -0.25],
            this.makePromoTexture());
    }

    buildCeilingLights(room) {
        const lightMaterial = new THREE.MeshStandardMaterial({
            color: 0xffffff,
            emissive: 0xecfff1,
            emissiveIntensity: 2.5,
            roughness: 0.4
        });
        for (const x of [-4.2, 0, 4.2]) {
            const fixture = this.addBox(room, [2.5, 0.12, 0.45], [x, 5.65, -0.5], lightMaterial, false);
            fixture.rotation.z = 0.015 * Math.sign(x);
        }
    }

    buildCheckout() {
        const checkout = new THREE.Group();
        this.scene.add(checkout);

        this.addBox(checkout, [6.6, 1.12, 1.35], [0.15, 0.58, 0.28], this.materials.greenDark);
        this.addBox(checkout, [6.82, 0.16, 1.55], [0.15, 1.2, 0.28], this.materials.cream);
        this.addBox(checkout, [2.85, 0.055, 0.82], [-1.02, 1.3, 0.48], this.materials.charcoal);
        this.addBox(checkout, [2.85, 0.035, 0.08], [-1.02, 1.34, 0.88], this.materials.metal);

        for (const x of [-2.6, -1.9, -1.2, -0.5]) {
            this.addBox(checkout, [0.07, 0.07, 0.72], [x, 1.34, 0.46], this.materials.black, false);
        }

        const register = new THREE.Group();
        register.position.set(1.35, 1.3, -0.12);
        checkout.add(register);
        this.addBox(register, [0.76, 0.68, 0.5], [0, 0.32, 0], this.materials.charcoal);
        const screen = this.addTextPlane(
            register,
            [0.66, 0.36],
            [0, 0.48, 0.26],
            this.makeRegisterTexture(),
            [-0.16, 0, 0]);
        screen.material.toneMapped = false;
        this.addBox(register, [0.88, 0.12, 0.68], [0, -0.02, 0.04], this.materials.black);

        const terminal = new THREE.Group();
        terminal.position.set(2.28, 1.34, 0.68);
        terminal.rotation.x = -0.22;
        terminal.rotation.y = -0.16;
        checkout.add(terminal);
        this.addBox(terminal, [0.38, 0.56, 0.2], [0, 0.24, 0], this.materials.charcoal);
        this.addBox(terminal, [0.29, 0.19, 0.018], [0, 0.34, 0.11], this.materials.green, false);
        for (let row = 0; row < 3; row++) {
            for (let column = 0; column < 3; column++) {
                this.addBox(
                    terminal,
                    [0.055, 0.035, 0.018],
                    [-0.08 + column * 0.08, 0.2 - row * 0.06, 0.112],
                    this.materials.metal,
                    false);
            }
        }

        const scanner = this.addBox(
            checkout,
            [0.78, 0.05, 0.45],
            [0.36, 1.3, 0.49],
            this.materials.black);
        const scannerGlass = this.addBox(
            checkout,
            [0.56, 0.025, 0.3],
            [0.36, 1.34, 0.49],
            this.materials.glass,
            false);
        scannerGlass.material = new THREE.MeshStandardMaterial({
            color: 0x5a1515,
            emissive: 0xff241c,
            emissiveIntensity: 0.35,
            roughness: 0.18
        });

        this.addTextPlane(
            checkout,
            [2.05, 0.52],
            [0.2, 0.58, 0.99],
            this.makeProductLabel("DZIĘKUJEMY!", "#00a651"));

        this.buildCheckoutProducts(checkout);
        return { group: checkout, scannerGlass, registerScreen: screen };
    }

    buildCheckoutProducts(checkout) {
        const coffee = new THREE.Group();
        this.addCylinder(coffee, 0.13, 0.16, 0.45, [0, 0.23, 0], this.materials.cream, 24);
        this.addCylinder(coffee, 0.15, 0.15, 0.055, [0, 0.48, 0], this.materials.charcoal, 24);
        this.addTextPlane(
            coffee,
            [0.22, 0.15],
            [0, 0.25, 0.153],
            this.makeProductLabel("KAWA", "#08743d"));
        coffee.position.set(-1.85, 1.34, 0.37);
        checkout.add(coffee);

        const sandwich = new THREE.Group();
        const bread = this.mesh(
            new THREE.BoxGeometry(0.48, 0.13, 0.38),
            this.materials.orange,
            [0, 0.08, 0]);
        bread.rotation.y = 0.2;
        sandwich.add(bread);
        this.addBox(sandwich, [0.5, 0.055, 0.4], [0, 0.15, 0], this.materials.green, false);
        sandwich.position.set(-0.9, 1.34, 0.42);
        checkout.add(sandwich);

        const bottle = new THREE.Group();
        this.addCylinder(bottle, 0.1, 0.12, 0.56, [0, 0.28, 0], this.materials.blue, 18);
        this.addCylinder(bottle, 0.05, 0.075, 0.16, [0, 0.64, 0], this.materials.blue, 14);
        this.addCylinder(bottle, 0.052, 0.052, 0.04, [0, 0.74, 0], this.materials.cream, 14);
        bottle.position.set(-2.42, 1.34, 0.47);
        bottle.rotation.z = -0.08;
        checkout.add(bottle);
    }

    buildCashier() {
        const cashier = new THREE.Group();
        cashier.position.set(0.2, 0, -1.2);
        this.scene.add(cashier);

        const torso = this.mesh(
            new THREE.SphereGeometry(0.76, 36, 24),
            this.materials.green,
            [0, 1.92, 0]);
        torso.scale.set(0.94, 1.14, 0.62);
        cashier.add(torso);
        const apron = this.mesh(
            new THREE.SphereGeometry(0.65, 30, 20),
            this.materials.greenDark,
            [0, 1.68, 0.36]);
        apron.scale.set(0.82, 0.8, 0.28);
        cashier.add(apron);
        this.addBox(cashier, [0.045, 0.9, 0.03], [-0.38, 1.87, 0.57], this.materials.yellow, false);
        this.addBox(cashier, [0.045, 0.9, 0.03], [0.38, 1.87, 0.57], this.materials.yellow, false);

        const collarLeft = this.mesh(
            new THREE.ConeGeometry(0.17, 0.42, 4),
            this.materials.cream,
            [-0.16, 2.4, 0.42]);
        collarLeft.rotation.z = -0.42;
        collarLeft.rotation.x = -0.15;
        cashier.add(collarLeft);
        const collarRight = collarLeft.clone();
        collarRight.position.x = 0.16;
        collarRight.rotation.z = 0.42;
        cashier.add(collarRight);

        this.addTextPlane(
            cashier,
            [0.42, 0.15],
            [0.26, 2.05, 0.67],
            this.makeNameTagTexture());

        const leftArm = new THREE.Group();
        this.addBone(leftArm, [-0.55, 2.18, 0.03], [-0.83, 1.78, 0.36], 0.16, this.materials.green);
        this.addBone(leftArm, [-0.83, 1.78, 0.36], [-0.58, 1.36, 0.83], 0.14, this.materials.skin);
        const leftHand = this.mesh(
            new THREE.SphereGeometry(0.17, 20, 14),
            this.materials.skinLight,
            [-0.5, 1.32, 0.88]);
        leftHand.scale.set(1.25, 0.52, 0.8);
        leftArm.add(leftHand);
        cashier.add(leftArm);

        const rightArm = new THREE.Group();
        rightArm.position.set(0.56, 2.18, 0.03);
        this.addBone(rightArm, [0, 0, 0], [0.24, -0.38, 0.18], 0.16, this.materials.green);
        this.addBone(rightArm, [0.24, -0.38, 0.18], [0.38, -0.72, 0.55], 0.14, this.materials.skin);
        const rightHand = this.mesh(
            new THREE.SphereGeometry(0.18, 20, 14),
            this.materials.skinLight,
            [0.4, -0.78, 0.6]);
        rightHand.scale.set(0.78, 1.18, 0.72);
        rightArm.add(rightHand);
        cashier.add(rightArm);

        const head = this.buildCashierHead(cashier);
        cashier.userData = {
            head,
            eyes: head.userData.eyes,
            mouth: head.userData.mouth,
            rightArm,
            rightArmRest: rightArm.rotation.clone()
        };
        return cashier;
    }

    buildCashierHead(cashier) {
        const head = new THREE.Group();
        head.position.set(0, 3.05, 0.05);

        this.addCylinder(head, 0.19, 0.22, 0.36, [0, -0.46, 0], this.materials.skin, 24);
        const face = this.mesh(
            new THREE.SphereGeometry(0.48, 38, 26),
            this.materials.skinLight,
            [0, 0, 0]);
        face.scale.set(0.96, 1.13, 0.9);
        head.add(face);

        const hairBack = this.mesh(
            new THREE.SphereGeometry(0.53, 34, 22),
            this.materials.hair,
            [0, 0.08, -0.12]);
        hairBack.scale.set(1.02, 1.13, 0.9);
        head.add(hairBack);
        const faceLayer = face.clone();
        faceLayer.position.z = 0.055;
        faceLayer.scale.set(0.91, 1.04, 0.82);
        head.add(faceLayer);

        const fringe = this.mesh(
            new THREE.SphereGeometry(0.45, 30, 18, 0, Math.PI * 2, 0, Math.PI / 2),
            this.materials.hairLight,
            [0, 0.34, 0.12]);
        fringe.scale.set(1.06, 0.62, 0.88);
        fringe.rotation.z = -0.05;
        head.add(fringe);
        const bun = this.mesh(
            new THREE.SphereGeometry(0.22, 24, 18),
            this.materials.hair,
            [0.34, 0.39, -0.31]);
        bun.scale.set(0.92, 1.08, 0.9);
        head.add(bun);

        [-1, 1].forEach(side => {
            const ear = this.mesh(
                new THREE.SphereGeometry(0.1, 18, 12),
                this.materials.skin,
                [side * 0.45, -0.02, 0.02]);
            ear.scale.set(0.65, 1.18, 0.58);
            head.add(ear);
        });

        const eyes = new THREE.Group();
        [-1, 1].forEach(side => {
            const white = this.mesh(
                new THREE.SphereGeometry(0.082, 20, 14),
                this.materials.eyeWhite,
                [side * 0.17, 0.08, 0.43]);
            white.scale.set(1.18, 0.7, 0.42);
            eyes.add(white);
            const iris = this.mesh(
                new THREE.SphereGeometry(0.038, 16, 12),
                this.materials.iris,
                [side * 0.17, 0.075, 0.49]);
            iris.scale.z = 0.48;
            eyes.add(iris);
            const brow = this.addBox(
                head,
                [0.21, 0.04, 0.04],
                [side * 0.17, 0.215, 0.43],
                this.materials.hair,
                false);
            brow.rotation.z = -side * 0.07;
        });
        head.add(eyes);

        const nose = this.mesh(
            new THREE.SphereGeometry(0.105, 20, 14),
            this.materials.skin,
            [0, -0.055, 0.49]);
        nose.scale.set(0.72, 1.2, 0.8);
        head.add(nose);
        [-1, 1].forEach(side => {
            const cheek = this.mesh(
                new THREE.SphereGeometry(0.105, 18, 12),
                this.materials.cheek,
                [side * 0.27, -0.13, 0.42]);
            cheek.scale.set(1.15, 0.5, 0.3);
            head.add(cheek);
        });

        const mouth = this.mesh(
            new THREE.SphereGeometry(0.13, 24, 16),
            this.materials.mouth,
            [0, -0.29, 0.47]);
        mouth.scale.set(1, 0.14, 0.43);
        head.add(mouth);
        const lowerLip = this.mesh(
            new THREE.SphereGeometry(0.105, 20, 12),
            this.materials.lip,
            [0, -0.33, 0.47]);
        lowerLip.scale.set(1, 0.18, 0.38);
        head.add(lowerLip);

        head.userData = { eyes, mouth, lowerLip };
        cashier.add(head);
        return head;
    }

    buildCustomerView() {
        const basket = new THREE.Group();
        basket.position.set(-3.25, 0.15, 2.4);
        basket.rotation.y = -0.18;
        this.scene.add(basket);
        this.addBox(basket, [1.5, 0.08, 0.85], [0, 0, 0], this.materials.green);
        this.addBox(basket, [1.5, 0.48, 0.07], [0, 0.24, -0.39], this.materials.green);
        this.addBox(basket, [1.5, 0.48, 0.07], [0, 0.24, 0.39], this.materials.green);
        this.addBox(basket, [0.07, 0.48, 0.78], [-0.72, 0.24, 0], this.materials.green);
        this.addBox(basket, [0.07, 0.48, 0.78], [0.72, 0.24, 0], this.materials.green);
        const handle = new THREE.TorusGeometry(0.57, 0.045, 12, 32, Math.PI);
        const handleMesh = this.mesh(handle, this.materials.charcoal, [0, 0.75, 0]);
        handleMesh.rotation.x = Math.PI / 2;
        basket.add(handleMesh);

        const hand = new THREE.Group();
        hand.position.set(2.55, 0.74, 2.25);
        hand.rotation.set(-0.2, -0.25, -0.12);
        this.scene.add(hand);
        const palm = this.mesh(
            new THREE.SphereGeometry(0.22, 20, 14),
            this.materials.skinLight,
            [0, 0, 0]);
        palm.scale.set(1.18, 0.42, 0.78);
        hand.add(palm);
        this.addBox(hand, [0.42, 0.25, 0.035], [-0.08, 0.16, 0.04], this.materials.green, false);
        this.addBox(hand, [0.11, 0.04, 0.04], [-0.14, 0.17, 0.065], this.materials.yellow, false);
    }

    buildLighting() {
        const hemisphere = new THREE.HemisphereLight(0xf1fff2, 0x657269, 1.85);
        this.scene.add(hemisphere);

        this.keyLight = new THREE.DirectionalLight(0xf6fff7, 3.1);
        this.keyLight.position.set(-4.2, 8, 5.8);
        this.keyLight.castShadow = true;
        this.keyLight.shadow.mapSize.set(1536, 1536);
        this.keyLight.shadow.camera.left = -7;
        this.keyLight.shadow.camera.right = 7;
        this.keyLight.shadow.camera.top = 7;
        this.keyLight.shadow.camera.bottom = -3;
        this.scene.add(this.keyLight);

        this.fillLight = new THREE.PointLight(0xbff5d1, 21, 9, 2);
        this.fillLight.position.set(3.2, 4.2, 2.2);
        this.scene.add(this.fillLight);

        this.scannerLight = new THREE.PointLight(0xff2a20, 3.4, 2.2, 2);
        this.scannerLight.position.set(0.36, 1.5, 0.65);
        this.scene.add(this.scannerLight);
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
        const { mouth, lowerLip } = this.cashier.userData.head.userData;
        const values = {
            closed: [1, 0.14, 0.43],
            narrow: [0.82, 0.5, 0.48],
            round: [0.65, 0.78, 0.66],
            open: [0.96, 0.7, 0.53]
        }[pose] || [1, 0.14, 0.43];
        mouth.scale.set(values[0], values[1], values[2]);
        lowerLip.position.y = pose === "closed" ? -0.33 : -0.37;
        lowerLip.scale.y = pose === "closed" ? 0.18 : 0.1;
    }

    cancel({ reset = false } = {}) {
        this.setTalking(false);
        this.pointerLook.set(0, 0);
        if (reset) {
            const { head, rightArm, rightArmRest } = this.cashier.userData;
            this.cashier.rotation.set(0, 0, 0);
            head.rotation.set(0, 0, 0);
            rightArm.rotation.copy(rightArmRest);
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
        this.smoothLook.lerp(this.pointerLook, Math.min(1, delta * 2.7));
        this.camera.position.x = this.smoothLook.x * 0.22;
        this.camera.position.y = 2.55 - this.smoothLook.y * 0.08;
        this.camera.lookAt(
            this.cameraTarget.x + this.smoothLook.x * 0.23,
            this.cameraTarget.y - this.smoothLook.y * 0.1,
            this.cameraTarget.z);

        const { head, eyes, rightArm, rightArmRest } = this.cashier.userData;
        this.cashier.position.y = reducedMotion ? 0 : Math.sin(elapsed * 1.2) * 0.008;
        head.rotation.y =
            this.smoothLook.x * 0.1
            + (reducedMotion ? 0 : Math.sin(elapsed * 0.46) * (this.talking ? 0.018 : 0.04));
        head.rotation.x =
            -this.smoothLook.y * 0.045
            + (this.talking && !reducedMotion ? Math.sin(elapsed * 4.1) * 0.025 : 0);
        head.rotation.z = this.talking && !reducedMotion
            ? Math.sin(elapsed * 2.7) * 0.018
            : 0;

        const blink = reducedMotion || Math.sin(elapsed * 2.35 + 0.5) <= 0.993 ? 1 : 0.07;
        eyes.scale.y += (blink - eyes.scale.y) * 0.45;

        rightArm.rotation.x = rightArmRest.x
            + (this.talking && !reducedMotion ? -0.12 - Math.sin(elapsed * 2.2) * 0.09 : 0);
        rightArm.rotation.z = rightArmRest.z
            + (this.talking && !reducedMotion ? -0.06 + Math.sin(elapsed * 2.2) * 0.08 : 0);
        rightArm.rotation.y = rightArmRest.y
            + (this.talking && !reducedMotion ? Math.sin(elapsed * 1.6) * 0.05 : 0);

        this.scannerLight.intensity = reducedMotion ? 2.8 : 2.7 + Math.sin(elapsed * 3.4) * 0.65;
        this.checkout.scannerGlass.material.emissiveIntensity = reducedMotion
            ? 0.28
            : 0.3 + Math.sin(elapsed * 3.4) * 0.08;
        this.fillLight.intensity = reducedMotion ? 20 : 20 + Math.sin(elapsed * 1.4) * 0.65;

        this.renderer.render(this.scene, this.camera);
    }
}

export function initializeKasiaThreeScenes(root = document) {
    root.querySelectorAll("[data-avatar-scene='kasia']").forEach(sceneElement => {
        if (controllers.has(sceneElement)) {
            return;
        }
        const host = sceneElement.querySelector("[data-kasia-three-host]");
        if (!host) {
            return;
        }
        try {
            controllers.set(sceneElement, new KasiaCashierThreeController(sceneElement, host));
        } catch (error) {
            host.querySelector("[data-kasia-three-loading]")?.setAttribute("hidden", "");
            const fallback = host.querySelector("[data-kasia-three-fallback]");
            if (fallback) {
                fallback.hidden = false;
            }
            console.error("The Three.js Żabka cashier scene could not be initialized.", error);
        }
    });
}

export function getKasiaThreeController(sceneElement) {
    return sceneElement ? controllers.get(sceneElement) || null : null;
}
