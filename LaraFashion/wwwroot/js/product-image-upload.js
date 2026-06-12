(function () {
    const MAX_IMAGE_SIDE = 1200;

    async function convertImageFileToPng(file) {
        if (!file || !file.type || !file.type.startsWith("image/")) {
            throw new Error("Invalid image file.");
        }

        const objectUrl = URL.createObjectURL(file);

        try {
            const image = await new Promise((resolve, reject) => {
                const img = new Image();
                img.onload = () => resolve(img);
                img.onerror = () => reject(new Error("Could not read image."));
                img.src = objectUrl;
            });

            let width = image.naturalWidth || image.width;
            let height = image.naturalHeight || image.height;

            if (!width || !height) {
                throw new Error("Invalid image dimensions.");
            }

            const ratio = Math.min(1, MAX_IMAGE_SIDE / Math.max(width, height));
            width = Math.max(1, Math.round(width * ratio));
            height = Math.max(1, Math.round(height * ratio));

            const canvas = document.createElement("canvas");
            canvas.width = width;
            canvas.height = height;

            const context = canvas.getContext("2d", { alpha: false });
            context.fillStyle = "#ffffff";
            context.fillRect(0, 0, width, height);
            context.drawImage(image, 0, 0, width, height);

            const blob = await new Promise((resolve, reject) => {
                canvas.toBlob((result) => {
                    if (result) resolve(result);
                    else reject(new Error("Could not convert image to PNG."));
                }, "image/png");
            });

            const originalName = file.name || "product-image";
            const cleanName = originalName.replace(/\.[^/.]+$/, "");
            const pngName = `${cleanName}.png`;

            return new File([blob], pngName, { type: "image/png" });
        } finally {
            URL.revokeObjectURL(objectUrl);
        }
    }

    window.laraFashionUploadProductImage = async function (input) {
        const statusElement = document.getElementById("productImageUploadStatus");
        const hiddenInput = document.getElementById("productImageUrlInput");

        try {
            if (!input || !input.files || input.files.length === 0) {
                return;
            }

            const originalFile = input.files[0];
            const token = localStorage.getItem("admin_token") || "";

            if (!token) {
                if (statusElement) statusElement.textContent = "يجب تسجيل الدخول قبل رفع الصورة.";
                return;
            }

            if (statusElement) {
                statusElement.textContent = "جاري تحويل الصورة إلى PNG وتصغيرها...";
                statusElement.style.color = "#0f766e";
            }

            const pngFile = await convertImageFileToPng(originalFile);

            if (pngFile.size > 10 * 1024 * 1024) {
                if (statusElement) {
                    statusElement.textContent = "الصورة كبيرة بعد التحويل. اختر صورة أصغر.";
                    statusElement.style.color = "#dc2626";
                }
                return;
            }

            if (statusElement) {
                statusElement.textContent = "جاري رفع الصورة...";
                statusElement.style.color = "#0f766e";
            }

            const formData = new FormData();
            formData.append("file", pngFile, pngFile.name);

            const response = await fetch("/api/admin/upload-product-image", {
                method: "POST",
                headers: {
                    "X-Admin-Token": token
                },
                body: formData
            });

            if (!response.ok) {
                let message = "فشل رفع الصورة.";
                try {
                    const error = await response.json();
                    if (error && error.message) message = error.message;
                } catch { }

                if (statusElement) {
                    statusElement.textContent = message;
                    statusElement.style.color = "#dc2626";
                }
                return;
            }

            const result = await response.json();
            const imageUrl = result.imageUrl || result.ImageUrl || "";

            if (!imageUrl) {
                if (statusElement) {
                    statusElement.textContent = "تم الرفع لكن لم يرجع رابط الصورة.";
                    statusElement.style.color = "#dc2626";
                }
                return;
            }

            if (hiddenInput) {
                hiddenInput.value = imageUrl;
                hiddenInput.dispatchEvent(new Event("change", { bubbles: true }));
            }

            input.setAttribute("data-uploaded-url", imageUrl);

            if (statusElement) {
                statusElement.textContent = "تم تحويل الصورة إلى PNG ورفعها. اضغط حفظ المنتج لتثبيتها.";
                statusElement.style.color = "#16a34a";
            }
        } catch (error) {
            if (statusElement) {
                statusElement.textContent = "فشل تحويل أو رفع الصورة. حاول اختيارها من الاستوديو أو اختر صورة أصغر.";
                statusElement.style.color = "#dc2626";
            }
            console.error("Product image upload failed", error);
        }
    };

    window.laraFashionGetUploadedProductImageUrl = function () {
        const hiddenInput = document.getElementById("productImageUrlInput");
        return hiddenInput ? hiddenInput.value : "";
    };
})();
