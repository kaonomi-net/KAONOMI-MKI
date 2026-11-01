"""
REST API of Fooocus (image generation, Stable Diffusion XL) for the Kaonomi server.

Runs in the Python environment of an installed Fooocus (the Pinokio one by default) and starts it without its web
interface: the same models, presets, styles and image log as the Fooocus app, behind a small FastAPI server.

    <fooocus>/env/bin/python fooocus_api.py [--fooocus <folder of Fooocus>] [--host 127.0.0.1] [--port 8888]

Routes:
    GET  /health     the server is up, the version of Fooocus, its default model, whether a generation is running
    GET  /options    the styles, performances, aspect ratios and models that /generate accepts, and the defaults
    POST /generate   generates images from a prompt (JSON body, see GenerateRequest); answers once they are done,
                     with their files (in the outputs folder of Fooocus) and, unless asked otherwise, their PNG / JPEG /
                     WebP data in base 64

Only text to image: the image inputs of Fooocus (upscale, vary, inpaint, image prompts, enhance) are left off.
"""

import argparse
import base64
import os
import random
import sys
import threading
import time
from typing import List, Optional

DEFAULT_FOOOCUS = os.path.join(os.path.expanduser("~"), "pinokio", "api", "fooocus.git", "app")


def start_fooocus(folder: str):
    """Runs the launcher of Fooocus (environment check, configuration, default models) without its web interface."""
    launcher = os.path.join(folder, "launch.py")
    if not os.path.isfile(launcher):
        sys.exit(f"Fooocus not found: {launcher} does not exist (use --fooocus)")
    # Fooocus reads its own command line: none of the API's arguments.
    sys.argv = [launcher]
    sys.path.insert(0, folder)
    os.chdir(folder)
    with open(launcher, encoding="utf-8") as file:
        source = file.read()
    source = source.replace("from webui import *", "")
    exec(compile(source, launcher, "exec"), {"__name__": "fooocus_launch", "__file__": launcher})


def main():
    parser = argparse.ArgumentParser(description="REST API of Fooocus for the Kaonomi server")
    parser.add_argument("--fooocus", default=os.environ.get("FOOOCUS_HOME", DEFAULT_FOOOCUS), help="folder of Fooocus (its launch.py)")
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=8888)
    options = parser.parse_args()

    start_fooocus(os.path.abspath(os.path.expanduser(options.fooocus)))

    # The worker greets "the app" of the web interface when it starts: here, this API.
    import shared
    from types import SimpleNamespace
    shared.gradio_root = SimpleNamespace(local_url=f"http://{options.host}:{options.port}/docs", server_name=options.host,
                                         server_port=options.port, share=False)

    # After the launcher: Fooocus' modules, its configuration and its worker thread (which loads the models).
    import args_manager
    import fooocus_version
    import modules.async_worker as worker
    import modules.config as config
    import modules.flags as flags
    import modules.sdxl_styles as sdxl_styles
    import uvicorn
    from fastapi import FastAPI, HTTPException
    from pydantic import BaseModel, Field

    class GenerateRequest(BaseModel):
        prompt: str
        negative_prompt: str = Field(default_factory=lambda: config.default_prompt_negative)
        styles: Optional[List[str]] = Field(default=None, description="Fooocus styles; null: the default ones")
        performance: Optional[str] = Field(default=None, description="Quality, Speed, Extreme Speed, Lightning, Hyper-SD")
        aspect_ratio: Optional[str] = Field(default=None, description="width*height, e.g. 1152*896")
        image_number: int = Field(default=1, ge=1, le=32)
        seed: int = Field(default=-1, description="-1: a random seed")
        output_format: str = "png"
        base_model: Optional[str] = None
        refiner_model: Optional[str] = None
        guidance_scale: Optional[float] = None
        sharpness: Optional[float] = None
        sampler: Optional[str] = None
        scheduler: Optional[str] = None
        include_base64: bool = True

    busy = threading.Lock()

    def task_arguments(request: GenerateRequest, seed: int) -> list:
        """The values of the controls of the Fooocus interface, in the order AsyncTask reads them."""
        ratio = (request.aspect_ratio or config.default_aspect_ratio.split(" ")[0]).replace("*", "×").replace("x", "×")
        loras = list(config.default_loras)[:config.default_max_lora_number]
        loras += [(False, "None", 1.0)] * (config.default_max_lora_number - len(loras))
        args = [False, request.prompt, request.negative_prompt,
                request.styles if request.styles is not None else list(config.default_styles),
                request.performance or config.default_performance, ratio, request.image_number,
                request.output_format, seed, False,
                request.sharpness if request.sharpness is not None else config.default_sample_sharpness,
                request.guidance_scale if request.guidance_scale is not None else config.default_cfg_scale,
                request.base_model or config.default_base_model_name,
                request.refiner_model or config.default_refiner_model_name, config.default_refiner_switch]
        for enabled, name, weight in loras:
            args += [enabled, name, weight]
        args += [False, "uov", flags.disabled, None, [], None, "", None]          # image input, upscale, inpaint
        args += [True, False, False, config.default_black_out_nsfw]               # no preview, intermediate results
        args += [1.5, 0.8, 0.3, config.default_cfg_tsnr, config.default_clip_skip]
        args += [request.sampler or config.default_sampler, request.scheduler or config.default_scheduler, config.default_vae]
        args += [config.default_overwrite_step, config.default_overwrite_switch, -1, -1, -1]
        args += [config.default_overwrite_upscale, False, False]
        args += [False, False, 64, 128]
        args += [flags.refiner_swap_method, 0.25]
        args += [False, 1.01, 1.02, 0.99, 0.95]                                  # FreeU off
        args += [False, False, config.default_inpaint_engine_version, 1.0, 0.618,
                 config.default_inpaint_advanced_masking_checkbox, config.default_invert_mask_checkbox, 0]
        if not args_manager.args.disable_image_log:
            args += [config.default_save_only_final_enhanced_image]
        if not args_manager.args.disable_metadata:
            args += [config.default_save_metadata_to_images, config.default_metadata_scheme]
        for _ in range(config.default_controlnet_image_count):                   # no image prompt
            args += [None, 0.5, 0.6, flags.default_ip]
        args += [False, 0, False, None, False, config.default_enhance_uov_method,
                 config.default_enhance_uov_processing_order, config.default_enhance_uov_prompt_type]
        for _ in range(config.default_enhance_tabs):                            # no enhancement
            args += [False, "", "", "", config.default_enhance_inpaint_mask_model,
                     config.default_inpaint_mask_cloth_category, config.default_inpaint_mask_sam_model,
                     0.25, 0.3, config.default_sam_max_detections, False,
                     config.default_inpaint_engine_version, 1.0, 0.618, 0, False]
        return args

    app = FastAPI(title="Fooocus API (Kaonomi)", version=fooocus_version.version)

    @app.get("/health")
    def health():
        return {"status": "ok", "fooocus": fooocus_version.version, "model": config.default_base_model_name,
                "busy": busy.locked()}

    @app.get("/options")
    def options_list():
        return {
            "styles": sdxl_styles.legal_style_names,
            "performances": flags.Performance.values(),
            "aspect_ratios": list(config.available_aspect_ratios),
            "models": list(config.model_filenames),
            "default_styles": list(config.default_styles),
            "default_performance": config.default_performance,
            "default_aspect_ratio": config.default_aspect_ratio.split(" ")[0].replace("×", "*"),
            "default_model": config.default_base_model_name,
        }

    @app.post("/generate")
    def generate(request: GenerateRequest):
        if not request.prompt.strip():
            raise HTTPException(status_code=400, detail="The prompt is empty")
        if request.output_format not in flags.output_formats:
            raise HTTPException(status_code=400, detail=f"output_format: one of {flags.output_formats}")
        seed = request.seed if request.seed >= 0 else random.randint(0, 2 ** 63 - 1)
        # One generation at a time (the GPU is shared); the others wait their turn.
        with busy:
            import ldm_patched.modules.model_management as model_management
            with model_management.interrupt_processing_mutex:
                model_management.interrupt_processing = False
            started = time.perf_counter()
            task = worker.AsyncTask(args=task_arguments(request, seed))
            worker.async_tasks.append(task)
            files = None
            while files is None:
                time.sleep(0.05)
                while task.yields:
                    flag, product = task.yields.pop(0)
                    if flag == "finish":
                        files = product
                if files is None and time.perf_counter() - started > 1800:
                    raise HTTPException(status_code=504, detail="The generation took more than 30 minutes")
        images = []
        for path in files:
            if not isinstance(path, str):
                continue
            image = {"path": os.path.abspath(path)}
            if request.include_base64 and os.path.isfile(path):
                with open(path, "rb") as file:
                    image["base64"] = base64.b64encode(file.read()).decode("ascii")
            images.append(image)
        if not images:
            raise HTTPException(status_code=500, detail="Fooocus returned no image (see its console)")
        return {"images": images, "seed": seed, "seconds": round(time.perf_counter() - started, 1),
                "model": request.base_model or config.default_base_model_name}

    print(f"[Fooocus API] http://{options.host}:{options.port} (docs: /docs)")
    uvicorn.run(app, host=options.host, port=options.port, log_level="info")


if __name__ == "__main__":
    main()
