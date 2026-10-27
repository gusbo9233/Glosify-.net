import { initializeSpeech, type SpeechApi } from './speech/controller.js';

declare global { interface Window { GlosifyTts: SpeechApi; } }
window.GlosifyTts = initializeSpeech();
