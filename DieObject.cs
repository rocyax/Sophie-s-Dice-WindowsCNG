private void Start()
	{
		this.result.dieSO = this.dieSO;
		this.rolledSinceLastRecord = true;
		this.forceFactorSin = CryptoRand.Range(0f, 5f);
		if (!this.isEditDie)
		{
			Vector3 vector = base.transform.localScale * this.dieSO.dieScale * this.dieShape.baseSizeMultiplier;
			if (this.dieShape.stretchable)
			{
				vector.z *= this.dieSO.stretch;
			}
			base.transform.localScale = vector;
		}
		if (this.dieSO.useSharpEdgeModel)
		{
			this.dieShape.dieMeshFilter.sharedMesh = this.dieShape.sharpEdgeModel;
			if (this.dieShape.secondaryDieMeshFilter)
			{
				this.dieShape.secondaryDieMeshFilter.sharedMesh = this.dieShape.secondarySharpEdgeModel;
			}
		}
		if (this.isEditDie)
		{
			this.dieRenderer.material = DiceShaders.GetMat(this.dieSO.surface_shader, this.dieSO.surface_metallic, this.dieSO.surface_smoothness, this.dieSO.surface_normalTexture, this.dieSO.surface_normalTextureScale, this.dieSO.marblePattern, this.dieSO.marblePatternScale, this.dieSO.surface_glitterscale);
			this.dieRenderer.material.color = this.dieSO.dieColor;
			this.dieRenderer.material.SetColor("_ColorB", this.dieSO.dieColorB);
			this.dieRenderer.material.SetFloat("_MarbleContrast", this.dieSO.marbleContrastValue);
		}
		else
		{
			if (this.dieSO.cachedMaterial == null)
			{
				this.dieSO.CacheMaterial();
			}
			this.dieRenderer.material = this.dieSO.cachedMaterial;
		}
		if (this.dieShape.secondaryDieMeshFilter != null)
		{
			if (this.dieSO.frameMat == DieFrameMaterial.Match)
			{
				this.dieRendererB.material = this.dieRenderer.material;
			}
			else
			{
				this.dieRendererB.material = DiceShaders.GetSecondaryMat((int)this.dieSO.frameMat);
			}
		}
		if (this.result.isExplodedChild)
		{
			this.SetExplosionForces(true);
		}
		this.rb.AddForce(this.spawnForce * CryptoRand.Range(0.8f, 1.2f), ForceMode.VelocityChange);
		this.RandomRotationAndSpin();
		if (this.spawnInHand)
		{
			this.rb.angularVelocity = Vector3.zero;
		}
		if (this.isEditDie)
		{
			global::UnityEngine.Object.Destroy(this.rb);
			base.transform.localEulerAngles = Vector3.zero;
			if (base.GetComponent<Collider>())
			{
				base.GetComponent<Collider>().enabled = false;
			}
			Collider[] componentsInChildren = base.gameObject.GetComponentsInChildren<Collider>();
			for (int i = 0; i < componentsInChildren.Length; i++)
			{
				componentsInChildren[i].enabled = false;
			}
		}
		this.KeepInBounds();
		if (this.rb.position.y < -0.2f)
		{
			this.rb.MovePosition(new Vector3(this.rb.position.x, -0.2f, this.rb.position.z));
		}
		this.ShuffleFaces();
	}

    public void ShuffleFaces()
{
    if (this.isEditDie)
    {
        return;
    }
    if (!this.dieShape.anisohedral && !this.dieShape.canShuffleBySymmetry)
    {
        return;
    }
    if (this.dieSO.hasCharacterBias || DieObject.characterBiasAllDice)
    {
        if (!this.dieShape.anisohedral)
        {
            this.RotateFaceToFace(this.keyFaceRotations[0], this.keyFaceRotations[0], 0f);
        }
        else
        {
            this.AnisohedralShuffle(true);
        }
        if (this.allowBiasChanges)
        {
            if (this.dieSO.weighting == Vector3.zero)
            {
                this.dieSO.MakeDiceCharacterBias();
            }
            this.rb.centerOfMass = this.dieSO.weighting;
        }
        this.result.isBiased = true;
        return;
    }
    if (this.result.isBiased)
    {
        if (this.allowBiasChanges)
        {
            this.rb.ResetCenterOfMass();
        }
        this.result.isBiased = false;
    }
    if (!this.dieShape.anisohedral)
    {
        this.RotateFaceToFace(
            this.keyFaceRotations[0],
            this.keyFaceRotations[CryptoRand.Range(0, this.keyFaceRotations.Count)],
            this.dieShape.faceAxisRotationalSymmetyAngle * (float)CryptoRand.Range(0, this.dieShape.faceAxisRotationalSymmetries)
        );
        return;
    }
    this.AnisohedralShuffle(false);
    }

	private void AnisohedralShuffle(bool resetFacePositions = false)
	{
		List<int> list = new List<int>();
		for (int i = 0; i < this.faces.Count; i++)
		{
			if (resetFacePositions)
			{
				list.Add(i);
			}
			else
			{
				list.Insert(CryptoRand.Range(0, list.Count + 1), i);
			}
		}
		for (int j = 0; j < list.Count; j++)
		{
			this.faces[j].transform.localPosition = this.faces[list[j]].getInitialFacePos;
			this.faces[j].transform.localRotation = this.faces[list[j]].getInitialFaceRot;
			this.faces[j].transform.localScale = this.faces[list[j]].getInitialFaceScale;
		}
		for (int k = 0; k < this.referenceFaces.Count; k++)
		{
			for (int l = 0; l < list.Count; l++)
			{
				if (this.referenceFaces[k].GetInitIndex() == list[l])
				{
					this.referenceFaces[k].referencedFaceIndex = l;
					if (this.referenceFaces[k].displayed)
					{
						this.referenceFaces[k].UpdateVisibleDisplay();
					}
				}
			}
		}
	    }

	private void DragUpdate()
	{
		if (this.isEditDie)
		{
			return;
		}
		if (this.beingDragged)
		{
			if (!this.result.pinned)
			{
				this.rollingTime = 0f;
				this.rb.angularDrag = 0.05f;
				this.result.PrepareForNewRoll(true);
				this.wasDragging = true;
				if (DieObject.dieDragStyle == DieDragStyle.Old)
				{
					this.rb.velocity = (DieDragger.dragPos - this.rb.position) * 6f;
					this.throwVelocity = this.rb.velocity;
					this.rb.velocity *= 1f + Mathf.Sin(this.forceFactorSin) * Mathf.InverseLerp(0f, 1f, (DieDragger.dragPos - this.rb.position).magnitude);
					this.rb.useGravity = false;
				}
				else if (DieObject.dieDragStyle == DieDragStyle.NewSphere)
				{
					this.throwVelocity = this.rb.velocity;
					float num = 0.25f;
					if (Vector3.Distance(this.rb.position, DieDragger.dragPos) > num)
					{
						Vector3 vector = DieDragger.dragPos - this.rb.position;
						Vector3 vector2 = DieDragger.dragPos - vector.normalized * num - this.rb.position;
						if (Vector3.Dot(this.rb.velocity.normalized, vector.normalized) > 0f)
						{
							this.rb.velocity = Vector3.Reflect(this.rb.velocity, vector.normalized) * 0.9f;
						}
						else
						{
							this.rb.velocity += vector2 * 15f;
						}
						this.rb.position = DieDragger.dragPos - vector.normalized * num;
					}
					this.rb.useGravity = true;
				}
				else
				{
					this.throwVelocity = this.rb.velocity;
					float num2 = 0.2f;
					Vector3 vector3 = this.rb.position;
					vector3.y = DieDragger.dragPos.y;
					if (Vector3.Distance(vector3, DieDragger.dragPos) > num2)
					{
						Vector3 vector4 = DieDragger.dragPos - vector3;
						Vector3 vector5 = DieDragger.dragPos - vector4.normalized * num2 - vector3;
						this.rb.velocity += vector5 * 15f;
						vector3 = DieDragger.dragPos - vector4.normalized * num2;
						vector3.y = this.rb.position.y;
						this.rb.position = vector3;
					}
					if (this.rb.position.y < DieDragger.dragPos.y)
					{
						this.rb.position = new Vector3(this.rb.position.x, DieDragger.dragPos.y, this.rb.position.z);
						if (this.rb.velocity.y < 0f)
						{
							Vector3.Reflect(this.rb.velocity, Vector3.up);
						}
					}
					this.rb.useGravity = true;
				}
			}
			else
			{
				Plane plane = new Plane(Vector3.up, this.rb.position);
				Ray ray = DownResify.MousePosRay();
				float num3 = 0f;
				plane.Raycast(ray, out num3);
				Vector3 vector6 = ray.origin + ray.direction * num3;
				this.rb.MovePosition(vector6);
			}
		}
		if (!this.beingDragged && this.wasDragging)
		{
			this.wasDragging = false;
			this.rb.useGravity = true;
			this.rb.velocity = this.throwVelocity * DieObject.throwForceMultiplier;
			this.rb.angularVelocity = CryptoRand.rotationUniform.eulerAngles * 0.017453292f * 3f;
			if (this.dieShape.isSpinner || DieObject.spinAllDice || (this.dieShape.isCoin && DieDragger.instance.GetDraggingCount() == 1))
			{
				this.RandomRotationAndSpin();
			}
		}
	}

	public void PopReroll(bool resetRerollCount = true)
	{
		this.ShuffleFaces();
		this.RandomRotationAndSpin();
		this.rb.AddForce(Vector3.up * CryptoRand.Range(3f, 4f), ForceMode.VelocityChange);
	}

    private void RandomRotationAndSpin()
	{
		if (this.dieShape.isSpinner || DieObject.spinAllDice)
		{
			Vector3 vector = new Vector3(90f, 0f, 0f);
			if (CryptoRand.value > 0.5f)
			{
				vector.x = -90f;
			}
			if (this.dieShape.spinningTop)
			{
				vector.x = -90f;
			}
			vector.z = CryptoRand.Range(-180f, 180f);
			base.transform.eulerAngles = vector;
			float num = CryptoRand.Range(50f, 100f);
			if (CryptoRand.value > 0.5f)
			{
				num *= -1f;
			}
			this.rb.angularVelocity = new Vector3(0f, num, 0f);
			return;
		}
		if (this.dieShape.isCoin)
		{
			base.transform.eulerAngles = new Vector3(CryptoRand.Range(-180f, 180f), CryptoRand.Range(-10f, 10f), CryptoRand.Range(-10f, 10f));
			float num2 = CryptoRand.Range(12f, 22f);
			this.rb.angularVelocity = new Vector3(num2, CryptoRand.Range(-5f, 5f), CryptoRand.Range(-5f, 5f));
			return;
		}
		base.transform.rotation = CryptoRand.rotationUniform;
		this.rb.angularVelocity = CryptoRand.rotationUniform.eulerAngles * 0.017453292f * 3f;
	}