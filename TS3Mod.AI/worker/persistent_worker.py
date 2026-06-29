#!/usr/bin/env python3
"""
Minimal persistent inference worker for TS3
- Loads PyTorch & Whisper model into VRAM once.
- Listens on HTTP for ultra-fast, overhead-free inference.
"""

import json
import os
import time
import argparse
import traceback
import warnings
from http.server import HTTPServer, BaseHTTPRequestHandler

PORT_DEFAULT = 5000
HOST_DEFAULT = '127.0.0.1'

MODEL = None

# Suppress standard warnings to keep the Unity console clean
warnings.filterwarnings("ignore", category=UserWarning)

def load_model():
    global MODEL
    try:
        import torch
        import whisper
        
        device = "cuda" if torch.cuda.is_available() else "cpu"
        
        print(f"[worker] GPU CUDA Available: {torch.cuda.is_available()}")
        print(f"[worker] Loading Whisper 'base.en' model to {device}...")
        
        # Load the model directly into VRAM
        # Note: You can change 'base.en' to 'small.en' if you want higher accuracy
        MODEL = whisper.load_model("base.en", device=device)
        
        print('[worker] Whisper model loaded successfully and ready.')
    except ImportError:
        print('[worker] ERROR: PyTorch or Whisper libraries are missing.')
        print('[worker] Please ensure they are installed in your Python environment.')
    except Exception as e:
        print('[worker] Model load failed:', e)
        traceback.print_exc()

def warmup():
    try:
        if MODEL is not None:
            import numpy as np
            print('[worker] Warming up VRAM allocation...')
            
            # Process 1 second of silent audio to pre-compile CUDA kernels
            # This prevents a latency spike on the very first voice command
            dummy_audio = np.zeros(16000, dtype=np.float32)
            MODEL.transcribe(dummy_audio)
            
            print('[worker] Warmup complete. System ready for ATC commands.')
    except Exception as e:
        print('[worker] Warmup failed:', e)

class SimpleHandler(BaseHTTPRequestHandler):
    def _set_json(self, code=200):
        self.send_response(code)
        self.send_header('Content-type', 'application/json')
        self.end_headers()

    def do_POST(self):
        if self.path == '/infer':
            content_length = int(self.headers.get('Content-Length', 0))
            post_data = self.rfile.read(content_length)
            
            try:
                req = json.loads(post_data.decode('utf-8'))
            except Exception:
                self._set_json(400)
                self.wfile.write(json.dumps({'error': 'invalid json payload'}).encode('utf-8'))
                return

            audio_path = req.get('audio_path')
            if not audio_path or not os.path.exists(audio_path):
                self._set_json(400)
                self.wfile.write(json.dumps({'error': 'audio_path not found or invalid'}).encode('utf-8'))
                return

            try:
                if MODEL is None:
                    self._set_json(500)
                    self.wfile.write(json.dumps({'error': 'Model not loaded. Check worker startup logs.'}).encode('utf-8'))
                    return
                    
                # Execute Ultra-Low Latency Inference
                start_time = time.time()
                
                # Transcribe the actual audio file sent by the game
                result = MODEL.transcribe(audio_path)
                text = result.get("text", "").strip()
                
                compute_time = (time.time() - start_time) * 1000
                print(f"[worker] Transcribed in {compute_time:.2f}ms: '{text}'")

                # Send the text back to C#
                self._set_json(200)
                self.wfile.write(json.dumps({'text': text}).encode('utf-8'))
                return
            
            except Exception as e:
                self._set_json(500)
                self.wfile.write(json.dumps({'error': str(e)}).encode('utf-8'))
                traceback.print_exc()
                return

        self._set_json(404)
        self.wfile.write(json.dumps({'error': 'endpoint not found'}).encode('utf-8'))

    # Suppress default HTTP logging to avoid spamming the terminal
    def log_message(self, format, *args):
        pass

def run_server(host, port):
    server = HTTPServer((host, port), SimpleHandler)
    print(f'[worker] Listening for TS3 inference requests on {host}:{port}')
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    except Exception:
        traceback.print_exc()
    finally:
        try:
            server.server_close()
        except Exception:
            pass

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--host', default=HOST_DEFAULT)
    parser.add_argument('--port', type=int, default=PORT_DEFAULT)
    args = parser.parse_args()
    
    load_model()
    warmup()
    run_server(args.host, args.port)

if __name__ == '__main__':
    main()